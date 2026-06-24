using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using sourcepp.vtfpp;

namespace BSPConvert.Lib
{
	// Tunables for the multi-pass scrolling shader -> flipbook conversion. These are plain numeric knobs;
	// the CLI maps its two friendly presets (resolution + playback accuracy) onto them.
	public class FlipbookOptions
	{
		public bool enabled = true;

		// Square frame size in pixels. 128 (DXT5/DXT1) is compact; 256 (BC7) is sharper but ~4x bytes/frame.
		public int resolution = 128;

		// Number of baked frames in the loop. More frames let the water play at (or nearer) Q3's true slow
		// scroll speed while staying smooth - at the cost of linearly larger files. See ComputeFps.
		public int frames = 240;

		// Lowest playback fps the auto-speed will allow. Q3 water often scrolls too slowly to animate smoothly
		// within a bounded frame count, so the detected scroll speed is increased until fps reaches this floor
		// (see FlipbookConverter.ComputeFps). More frames reach the floor at a speed closer to Q3's true rate.
		public int minFps = 12;

		// When true, translucency is derived per-texel from the source images: their alpha channel if they
		// have one, otherwise their luminance (bright caustics opaque, dark gaps see-through - matching the
		// Q3 "GL_dst_color GL_one" brightening). This bakes an alpha channel, so 128px uses DXT5 instead of
		// DXT1 (~2x size). When false, a flat constant alpha is used instead (DXT1-friendly).
		public bool autoAlpha = true;

		// Translucency control. With autoAlpha it scales the per-texel alpha (1 = full luminance range);
		// without it, it's the flat constant alpha (1 = opaque, lower = more see-through).
		public float alpha = 1.0f;

		// Multiplier on the auto-detected scroll speed (from the shader's tcmod scroll). 1.0 = detected speed;
		// >1 faster, <1 slower. Normally left at 1.0 (speed is automatic); exposed for programmatic fine-tuning.
		public float speed = 1.0f;

		// Explicit playback fps override; null = derive automatically from the scroll rate (see ComputeFps).
		public int? fps;

		// Tiles the fastest scrolling layer travels per loop. >=2 keeps slower layers from rounding to a
		// standstill so each layer still moves in its own direction.
		public int maxLoopTiles = 2;

		// Max bake resolution (px) for a layered blend-stack composite (the complex multi-blend animMap path).
		// Unlike a plain animMap, these loops can run to ~256 frames, so a large source (e.g. a 1024px decal)
		// would balloon the file; the composite is capped to this and downsampled. The animated detail in these
		// effects is usually low-res anyway. 0 = no cap (use the largest source size).
		public int layeredResolution = 256;
	}

	// Converts a Q3 multi-pass scrolling shader (e.g. baseq3 liquids water - several "GL_dst_color"
	// scrolling layers over a lightmap) into a single looping flipbook VTF plus an AnimatedTexture VMT.
	// Source has no shader that reproduces the multi-pass blend live, so the layers are pre-composited
	// per frame here with ImageSharp and baked into one animated texture; the lightmap multiply is left
	// to the engine via LightmappedGeneric.
	public class FlipbookConverter
	{
		private readonly string pk3Dir;
		private readonly FlipbookOptions options;
		private readonly Func<string, string?> resolveImagePath;

		public FlipbookConverter(string pk3Dir, FlipbookOptions options, Func<string, string?> resolveImagePath)
		{
			this.pk3Dir = pk3Dir;
			this.options = options;
			this.resolveImagePath = resolveImagePath;
		}

		// One scrolling pass: its source pixels plus the per-loop tile offset used to reproduce its scroll.
		private class Layer
		{
			public Rgba32[] pixels = Array.Empty<Rgba32>();
			public int width;
			public int height;
			public bool hasAlpha;               // source image carries a real (non-opaque) alpha channel
			public Vector2 scale = Vector2.One; // tcmod scale magnitude, reproduced via the VMT (not baked - see SampleLayer)
			public Vector2 scrollTiles;         // whole tiles this layer travels over the full loop (keeps it seamless)
		}

		// Bakes the flipbook + writes the VMT if the shader matches the multi-pass scrolling pattern.
		// Returns false (leaving the shader for the normal material path) when disabled, unmatched, or a
		// source image is missing.
		public bool TryConvert(string textureName, Shader shader)
		{
			if (!options.enabled)
				return false;

			// animMap shaders are explicit frame sequences (e.g. fire), which map directly to a flipbook.
			if (IsAnimMapShader(shader))
				return ConvertAnimMap(textureName, shader);

			// Q3 layered liquids (every visible stage scrolls with a dst-color blend and there's no opaque
			// base) get the dedicated translucent screen-blend bake - check it before the general path,
			// since their scrolling stages would otherwise match the opaque-surface effect gate below.
			if (IsMultiPassScrollingShader(shader))
				return ConvertMultiPassScrolling(textureName, shader);

			// Any opaque surface carrying animated overlay(s) that no single Source material can express -
			// scrolling lights along a wall, a rotating vortex over a jump pad, a pulsing glow - is replayed
			// stage by stage into a looping flipbook by the ordered blend-stack compositor.
			if (IsLayeredEffectShader(shader))
				return ConvertLayeredStack(textureName, shader, GetTextureStages(shader));

			return false;
		}

		// Bakes a Q3 layered liquid (the multi-pass dst-color scrolling pattern) into a translucent
		// flipbook: each layer's seamless tile is screen-composited per frame and the shared tcmod scale is
		// reapplied on the surface via the VMT so ripple size matches Q3.
		private bool ConvertMultiPassScrolling(string textureName, Shader shader)
		{
			var layers = LoadLayers(shader, out var maxScrollRate);
			if (layers == null)
				return false;

			var frames = BakeFrames(layers);

			var vtfPath = Path.Combine(pk3Dir, textureName + ".vtf");
			Directory.CreateDirectory(Path.GetDirectoryName(vtfPath)!);
			if (!BakeVtf(vtfPath, frames, options.resolution, options.resolution, GetOutputFormat()))
				return false;

			var commonScale = new Vector2(layers.Average(l => l.scale.X), layers.Average(l => l.scale.Y));
			WriteVmt(textureName, commonScale, ComputeFps(maxScrollRate));
			return true;
		}

		// Playback fps for the AnimatedTexture proxy. The loop scrolls maxLoopTiles tiles, so
		// fps = Frames * scrollSpeed / maxLoopTiles. We scroll at the shader's detected rate, but raise it
		// until fps hits options.minFps - i.e. only speed water up past Q3's true rate when the frame budget
		// can't animate it smoothly otherwise.
		private int ComputeFps(float maxScrollRate)
		{
			if (options.fps.HasValue)
				return Math.Clamp(options.fps.Value, 1, 30);

			var minSpeed = (float)options.minFps * options.maxLoopTiles / options.frames;
			var scrollSpeed = MathF.Max(maxScrollRate, minSpeed) * options.speed;
			var fps = (int)MathF.Round(options.frames * scrollSpeed / options.maxLoopTiles);

			return Math.Clamp(fps, 1, 30);
		}

		// Matches shaders made of >= 2 visible scrolling layers that all use a dst-color blend - the
		// signature of Q3's layered caustic/ripple water. Deliberately narrow so ordinary multi-stage
		// shaders aren't swept up.
		private bool IsMultiPassScrollingShader(Shader shader)
		{
			var textureStages = GetTextureStages(shader);
			if (textureStages.Count < 2)
				return false;

			return textureStages.All(s =>
				s.bundles[0].texMods.Any(t => t.type == TexMod.TMOD_SCROLL) &&
				(s.flags & ShaderStageFlags.GLS_SRCBLEND_BITS) == ShaderStageFlags.GLS_SRCBLEND_DST_COLOR);
		}

		private static List<ShaderStage> GetTextureStages(Shader shader)
		{
			return shader.GetImageStages()
				.Where(s => s.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED &&
					s.bundles[0].tcGen != TexCoordGen.TCGEN_LIGHTMAP)
				.ToList();
		}

		// Matches shaders with an animMap stage - an explicit frame sequence (Q3 "animMap <fps> f1 f2 ...").
		private bool IsAnimMapShader(Shader shader)
		{
			return GetTextureStages(shader).Any(s => s.bundles[0].numImageAnimations > 1);
		}

		// Matches an opaque surface carrying one or more animated overlays that no single Source material can
		// reproduce: a static base plus stage(s) that scroll (lights tracking along a wall), rotate (a vortex
		// over a jump pad), stretch/pulse via a waveform, etc. The whole blend stack is composited per frame
		// into a looping flipbook by ConvertLayeredStack. (animMap stacks and layered liquids are handled by
		// their own paths before this is reached.)
		private bool IsLayeredEffectShader(Shader shader)
		{
			var stages = GetTextureStages(shader);
			if (stages.Count < 2)
				return false;

			// At least one stage must animate in a way the compositor can actually reproduce: scroll, rotate,
			// stretch, or an rgb/alpha waveform. Transform/turbulent shear isn't replayed, so a stage animated
			// only by those doesn't count (it would bake to a static frame).
			var animates = stages.Any(s =>
				(s.rgbGen == ColorGen.CGEN_WAVEFORM && s.rgbWave.frequency > 0f) ||
				(s.alphaGen == AlphaGen.AGEN_WAVEFORM && s.alphaWave.frequency > 0f) ||
				s.bundles[0].texMods.Any(t =>
					t.type == TexMod.TMOD_SCROLL ||
					t.type == TexMod.TMOD_ROTATE ||
					(t.type == TexMod.TMOD_STRETCH && t.wave.frequency > 0f)));
			if (!animates)
				return false;

			// A stack with an opaque base bakes to a self-contained opaque flipbook (ConvertLayeredStack
			// overwrites the framebuffer with that base). A stack with no opaque base can still bake when every
			// stage is additive ("GL_one GL_one"): the layers sum from black into an additive flipbook (black =
			// transparent, emitted as $additive), e.g. a static glow under a scrolling glow. Mixed alpha-only
			// overlay stacks aren't reproduced here - those stay on the normal path. A single animated stage is
			// better served live by MaterialConverter's texture-transform proxies, which the >= 2 stage
			// requirement above already keeps out.
			return stages.Any(s => !IsOverlayBlend(s)) || stages.All(IsAdditiveBlend);
		}

		// Bakes a Q3 animMap (explicit frame sequence, e.g. a fire effect) into a flipbook VTF - one VTF frame
		// per animMap frame, played by an AnimatedTexture proxy at the animMap's own frequency. Each animMap
		// stage advances through its own frame list; plain "map" stages stay constant. Additive shaders (the
		// common sfx case, "GL_one GL_one") sum every stage per frame; otherwise the first animMap stage is
		// used as an opaque animated base.
		private bool ConvertAnimMap(string textureName, Shader shader)
		{
			var stages = GetTextureStages(shader);
			var animStages = stages.Where(s => s.bundles[0].numImageAnimations > 1).ToList();
			if (animStages.Count == 0)
				return false;

			// A non-additive overlay (a multiply/filter mask or an alpha layer) can't be reproduced by the
			// simple additive-sum below. That's a complex layered effect (e.g. masked ring animations over a
			// decal) - hand it to the full ordered blend-stack compositor, which replays each stage's real
			// blendFunc, animMap, tcMod stretch and rgbGen wave into a looping flipbook.
			if (stages.Skip(1).Any(s => !IsAdditiveBlend(s)))
				return ConvertLayeredStack(textureName, shader, stages);

			// The surface is additive (black = transparent) only when every stage is an additive/overlay glow.
			// If there's an opaque base stage (e.g. a lit launchpad texture under additive arrow/dot overlays),
			// the surface is opaque - the overlays just brighten it - so $additive must NOT be emitted.
			var anyAdditive = stages.Any(IsAdditiveBlend);
			var hasOpaqueBase = stages.Any(s => !IsOverlayBlend(s));
			var isAdditive = anyAdditive && !hasOpaqueBase;
			var compositeStages = anyAdditive ? stages : new List<ShaderStage> { animStages[0] };

			var frameCount = animStages.Max(s => s.bundles[0].numImageAnimations);
			var fps = Math.Clamp((int)MathF.Round(animStages[0].bundles[0].imageAnimationSpeed), 1, 30);

			// animMap frames are authored at a specific size, so bake at the original resolution (taken from the
			// first frame) rather than --waterres. Any mismatched frame is resized to match (frames in one VTF
			// must share dimensions).
			int width = 0, height = 0;
			var stageFrames = new List<Rgba32[][]>();
			foreach (var stage in compositeStages)
			{
				var bundle = stage.bundles[0];
				var count = bundle.numImageAnimations > 1 ? bundle.numImageAnimations : 1;
				var buffers = new Rgba32[count][];
				for (var i = 0; i < count; i++)
				{
					var imagePath = resolveImagePath(Path.ChangeExtension(bundle.images[i], null));
					if (imagePath == null || !File.Exists(imagePath))
						return false; // missing a frame - leave it for the normal path

					using var image = Image.Load<Rgba32>(imagePath);
					if (width == 0)
					{
						width = image.Width;
						height = image.Height;
					}
					if (image.Width != width || image.Height != height)
						image.Mutate(x => x.Resize(width, height));

					var pixels = new Rgba32[width * height];
					image.CopyPixelDataTo(pixels);
					buffers[i] = pixels;
				}
				stageFrames.Add(buffers);
			}

			var pixelCount = width * height;
			var frames = new List<byte[]>(frameCount);
			if (stageFrames.Count == 1)
			{
				// Single animation stage: convert its frame images directly (no compositing needed). Rgba32 is
				// laid out R,G,B,A, so the buffer is already a valid RGBA8888 frame - just copy it.
				var buffers = stageFrames[0];
				for (var f = 0; f < frameCount; f++)
				{
					var frame = new byte[pixelCount * 4];
					MemoryMarshal.AsBytes(buffers[f % buffers.Length].AsSpan()).CopyTo(frame);
					frames.Add(frame);
				}
			}
			else
			{
				// Multiple stages: additively sum each stage's current frame per pixel.
				for (var f = 0; f < frameCount; f++)
				{
					var frame = new byte[pixelCount * 4];
					for (var p = 0; p < pixelCount; p++)
					{
						var color = Vector3.Zero;
						foreach (var buffers in stageFrames)
						{
							var pixel = buffers[buffers.Length > 1 ? f % buffers.Length : 0][p];
							color += new Vector3(pixel.R, pixel.G, pixel.B) / 255f;
						}
						color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);

						var index = p * 4;
						frame[index + 0] = (byte)(color.X * 255f + 0.5f);
						frame[index + 1] = (byte)(color.Y * 255f + 0.5f);
						frame[index + 2] = (byte)(color.Z * 255f + 0.5f);
						frame[index + 3] = 255;
					}
					frames.Add(frame);
				}
			}

			var vtfPath = Path.Combine(pk3Dir, textureName + ".vtf");
			Directory.CreateDirectory(Path.GetDirectoryName(vtfPath)!);
			// RGB only (additive uses black=transparent; opaque base needs no alpha), so a no-alpha format is compact.
			var format = Math.Max(width, height) >= 256 ? ImageFormat.STRATA_BC7 : ImageFormat.DXT1;
			if (!BakeVtf(vtfPath, frames, width, height, format))
				return false;

			WriteAnimMapVmt(textureName, shader, fps, isAdditive);
			return true;
		}

		// Playback fps and the max baked frame count for layered stacks. 30fps reads smoothly; the cap bounds
		// file size - when a stack's rounded loop needs more frames than this, it's truncated (a slight seam at
		// the loop point) rather than dropping layers or slowing playback.
		private const int LayeredFps = 30;
		private const int LayeredMaxFrames = 256;

		// One stage of a layered blend stack: its frame image(s) plus everything needed to replay it over time.
		private class StackLayer
		{
			public Rgba32[][] frames = Array.Empty<Rgba32[]>(); // one buffer per animMap frame (>=1)
			public int width;
			public int height;
			public float animSpeed;                  // animMap fps (0 => single static image)
			public bool clamp;                        // clampMap => clamp texcoords instead of wrapping
			public List<TexModInfo> texMods = new();
			public ColorGen rgbGen;
			public WaveForm rgbWave = new();
			public AlphaGen alphaGen;
			public WaveForm alphaWave = new();
			public byte[] constantColor = new byte[4];
			public ShaderStageFlags flags;            // src/dst blend factors
		}

		// Bakes a complex multi-blend shader (opaque base + additive/multiply/mask overlays, each possibly
		// animMap-cycled and tcMod-stretched) into one looping flipbook by replaying Q3's fixed-function blend
		// stack per pixel per frame. The opaque base stage makes the result self-contained (independent of the
		// scene behind the surface), so it bakes to an opaque RGB VTF; the lightmap multiply is left to the engine.
		private bool ConvertLayeredStack(string textureName, Shader shader, List<ShaderStage> stages)
		{
			var layers = new List<StackLayer>();
			int bakeW = 0, bakeH = 0;
			foreach (var stage in stages)
			{
				var bundle = stage.bundles[0];
				var count = bundle.numImageAnimations > 1 ? bundle.numImageAnimations : 1;
				var buffers = new Rgba32[count][];
				int lw = 0, lh = 0;
				for (var i = 0; i < count; i++)
				{
					var imagePath = resolveImagePath(Path.ChangeExtension(bundle.images[i], null));
					if (imagePath == null || !File.Exists(imagePath))
						return false; // missing a frame - leave it for the normal path (static decal)

					using var image = Image.Load<Rgba32>(imagePath);
					if (lw == 0) { lw = image.Width; lh = image.Height; }
					if (image.Width != lw || image.Height != lh)
						image.Mutate(x => x.Resize(lw, lh)); // frames of one stage must share size

					var pixels = new Rgba32[lw * lh];
					image.CopyPixelDataTo(pixels);
					buffers[i] = pixels;
				}

				layers.Add(new StackLayer
				{
					frames = buffers,
					width = lw,
					height = lh,
					animSpeed = bundle.numImageAnimations > 1 ? bundle.imageAnimationSpeed : 0f,
					clamp = bundle.clamp,
					texMods = bundle.texMods,
					rgbGen = stage.rgbGen,
					rgbWave = stage.rgbWave,
					alphaGen = stage.alphaGen,
					alphaWave = stage.alphaWave,
					constantColor = stage.constantColor,
					flags = stage.flags
				});

				bakeW = Math.Max(bakeW, lw);
				bakeH = Math.Max(bakeH, lh);
			}

			if (bakeW == 0 || bakeH == 0)
				return false;

			// Cap the composite size (a layered loop can be ~256 frames, so a 1024px source would balloon the
			// file). Downsample proportionally; we composite directly at bake size by sampling sources via UV.
			var cap = options.layeredResolution;
			var maxDim = Math.Max(bakeW, bakeH);
			if (cap > 0 && maxDim > cap)
			{
				bakeW = Math.Max(1, bakeW * cap / maxDim);
				bakeH = Math.Max(1, bakeH * cap / maxDim);
			}

			// DXT compression needs dimensions that are multiples of 4.
			bakeW = (bakeW + 3) & ~3;
			bakeH = (bakeH + 3) & ~3;

			var (frameCount, fps) = ComputeLayeredLoop(layers);
			var pixelCount = bakeW * bakeH;
			var frames = new List<byte[]>(frameCount);

			for (var f = 0; f < frameCount; f++)
			{
				var t = (float)f / fps; // seconds into the loop
				var frame = new byte[pixelCount * 4];
				for (var y = 0; y < bakeH; y++)
				{
					for (var x = 0; x < bakeW; x++)
					{
						var uv = new Vector2((x + 0.5f) / bakeW, (y + 0.5f) / bakeH);

						// Replay the blend stack onto the framebuffer (starts black; the opaque base stage
						// overwrites it, then each overlay composites with its real blendFunc).
						var fb = Vector3.Zero;
						foreach (var layer in layers)
						{
							var animIdx = layer.animSpeed > 0f && layer.frames.Length > 1
								? (int)MathF.Floor(t * layer.animSpeed) % layer.frames.Length
								: 0;
							var st = TransformTexcoord(uv, layer.texMods, t);
							var texel = SampleTexel(layer.frames[animIdx], layer.width, layer.height, st, layer.clamp);

							var src = new Vector3(texel.X, texel.Y, texel.Z) * EvalRgb(layer, t);
							var srcAlpha = EvalAlpha(layer, texel.W, t);
							fb = BlendStage(layer.flags, src, srcAlpha, fb);
							fb = Vector3.Clamp(fb, Vector3.Zero, Vector3.One); // Q3's 8-bit framebuffer clamps each pass
						}

						var index = (y * bakeW + x) * 4;
						frame[index + 0] = (byte)(fb.X * 255f + 0.5f);
						frame[index + 1] = (byte)(fb.Y * 255f + 0.5f);
						frame[index + 2] = (byte)(fb.Z * 255f + 0.5f);
						frame[index + 3] = 255; // opaque base bakes opaque; additive stacks ignore alpha ($additive)
					}
				}
				frames.Add(frame);
			}

			var vtfPath = Path.Combine(pk3Dir, textureName + ".vtf");
			Directory.CreateDirectory(Path.GetDirectoryName(vtfPath)!);
			var format = Math.Max(bakeW, bakeH) >= 256 ? ImageFormat.STRATA_BC7 : ImageFormat.DXT1;
			if (!BakeVtf(vtfPath, frames, bakeW, bakeH, format))
				return false;

			// With no opaque base the stack is all additive overlays (see IsLayeredEffectShader), so the
			// composite is an additive glow - emit $additive 1 so the baked black reads back as transparent.
			var isAdditive = !stages.Any(s => !IsOverlayBlend(s));
			WriteAnimMapVmt(textureName, shader, fps, isAdditive);
			return true;
		}

		// Loop length for a layered stack. Each animated element (animMap, tcMod stretch, rgbGen/alphaGen wave)
		// has its own period; the loop is their LCM. The periods are first snapped to a shared base so the LCM
		// stays small (a tiny speed adjustment per layer), then the frame count is derived at LayeredFps and
		// capped at LayeredMaxFrames (truncating very long loops with a slight seam rather than growing the file).
		private static (int frameCount, int fps) ComputeLayeredLoop(List<StackLayer> layers)
		{
			var periods = new List<float>();
			foreach (var layer in layers)
			{
				if (layer.animSpeed > 0f && layer.frames.Length > 1)
					periods.Add(layer.frames.Length / layer.animSpeed);

				foreach (var texMod in layer.texMods)
				{
					if ((texMod.type == TexMod.TMOD_STRETCH || texMod.type == TexMod.TMOD_TURBULENT) && texMod.wave.frequency > 0f)
						periods.Add(1f / texMod.wave.frequency);

					// A scroll wraps seamlessly once it has travelled a whole tile, so its period is the
					// time per tile (one per moving axis). A rotate's period is one full revolution.
					else if (texMod.type == TexMod.TMOD_SCROLL)
					{
						if (MathF.Abs(texMod.scroll[0]) > 1e-4f)
							periods.Add(1f / MathF.Abs(texMod.scroll[0]));
						if (MathF.Abs(texMod.scroll[1]) > 1e-4f)
							periods.Add(1f / MathF.Abs(texMod.scroll[1]));
					}
					else if (texMod.type == TexMod.TMOD_ROTATE && MathF.Abs(texMod.rotateSpeed) > 1e-4f)
						periods.Add(360f / MathF.Abs(texMod.rotateSpeed));
				}

				if (layer.rgbGen == ColorGen.CGEN_WAVEFORM && layer.rgbWave.frequency > 0f)
					periods.Add(1f / layer.rgbWave.frequency);
				if (layer.alphaGen == AlphaGen.AGEN_WAVEFORM && layer.alphaWave.frequency > 0f)
					periods.Add(1f / layer.alphaWave.frequency);
			}

			if (periods.Count == 0)
				return (1, LayeredFps); // nothing animates (shouldn't happen on this path) - single frame

			// Snap every period to an integer multiple of the smallest one, then the loop is base * LCM(multiples).
			var quantum = periods.Min();
			var multiples = periods.Select(p => Math.Clamp((int)MathF.Round(p / quantum), 1, 1000)).ToList();
			var lcm = multiples.Aggregate(1, Lcm);
			var loopSeconds = lcm * quantum;

			var frameCount = Math.Clamp((int)MathF.Round(loopSeconds * LayeredFps), 1, LayeredMaxFrames);
			return (frameCount, LayeredFps);
		}

		private static int Gcd(int a, int b)
		{
			while (b != 0)
				(a, b) = (b, a % b);
			return a;
		}

		// LCM with an upper clamp so a near-incommensurate set can't explode the frame budget (the caller caps frames).
		private static int Lcm(int a, int b)
		{
			if (a == 0 || b == 0)
				return Math.Max(a, b);
			var lcm = (long)(a / Gcd(a, b)) * b;
			return (int)Math.Min(lcm, 100000);
		}

		// Q3 texcoord modifiers applied in order, evaluated at time t. Reproduces scale/scroll/stretch/rotate
		// (transform/turbulent shear aren't reproduced). Stretch and rotate are what animate these effect layers.
		private static Vector2 TransformTexcoord(Vector2 st, List<TexModInfo> texMods, float t)
		{
			foreach (var texMod in texMods)
			{
				switch (texMod.type)
				{
					case TexMod.TMOD_SCALE:
						st = new Vector2(st.X * texMod.scale[0], st.Y * texMod.scale[1]);
						break;
					case TexMod.TMOD_SCROLL:
						st += new Vector2(texMod.scroll[0], texMod.scroll[1]) * t;
						break;
					case TexMod.TMOD_STRETCH:
					{
						// Q3 scales texcoords about (0.5,0.5) by 1/wave (a pulsing zoom).
						var wave = EvalWave(texMod.wave, t);
						var p = 1f / (MathF.Abs(wave) < 0.01f ? (wave < 0f ? -0.01f : 0.01f) : wave);
						st = (st - new Vector2(0.5f)) * p + new Vector2(0.5f);
						break;
					}
					case TexMod.TMOD_ROTATE:
					{
						var rad = texMod.rotateSpeed * t * (MathF.PI / 180f);
						var c = MathF.Cos(rad);
						var s = MathF.Sin(rad);
						var d = st - new Vector2(0.5f);
						st = new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c) + new Vector2(0.5f);
						break;
					}
				}
			}
			return st;
		}

		// rgbGen color multiplier for a stage at time t (waveform pulse or constant color; otherwise white).
		private static Vector3 EvalRgb(StackLayer layer, float t)
		{
			if (layer.rgbGen == ColorGen.CGEN_WAVEFORM)
			{
				var v = Math.Clamp(EvalWave(layer.rgbWave, t), 0f, 1f);
				return new Vector3(v);
			}
			if (layer.rgbGen == ColorGen.CGEN_CONST)
				return new Vector3(layer.constantColor[0], layer.constantColor[1], layer.constantColor[2]) / 255f;

			return Vector3.One;
		}

		// Per-stage source alpha at time t (used only by alpha-factor blends; the texel's own alpha by default).
		private static float EvalAlpha(StackLayer layer, float texelAlpha, float t)
		{
			if (layer.alphaGen == AlphaGen.AGEN_WAVEFORM)
				return Math.Clamp(EvalWave(layer.alphaWave, t), 0f, 1f);
			if (layer.alphaGen == AlphaGen.AGEN_CONST)
				return layer.constantColor[3] / 255f;

			return texelAlpha;
		}

		private static float EvalWave(WaveForm wave, float t)
		{
			var x = t * wave.frequency + wave.phase;
			var frac = x - MathF.Floor(x);
			float shape = wave.func switch
			{
				GenFunc.GF_SIN => MathF.Sin(frac * 2f * MathF.PI),
				GenFunc.GF_SAWTOOTH => frac,
				GenFunc.GF_INVERSE_SAWTOOTH => 1f - frac,
				GenFunc.GF_TRIANGLE => frac < 0.5f ? frac * 2f : 2f - frac * 2f,
				GenFunc.GF_SQUARE => frac < 0.5f ? 1f : -1f,
				_ => 1f
			};
			return wave.base_ + wave.amplitude * shape;
		}

		// Replays one Q3 blendFunc: result = src*srcFactor + dst*dstFactor. No blend bits set means an opaque
		// base ("GL_one GL_zero"), which simply overwrites the framebuffer.
		private static Vector3 BlendStage(ShaderStageFlags flags, Vector3 src, float srcAlpha, Vector3 dst)
		{
			var srcBits = flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBits = flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			if (srcBits == 0 && dstBits == 0)
				return src; // opaque replace

			var sf = SrcFactor(srcBits, src, srcAlpha, dst);
			var df = DstFactor(dstBits, src, srcAlpha, dst);
			return src * sf + dst * df;
		}

		private static Vector3 SrcFactor(ShaderStageFlags bits, Vector3 src, float a, Vector3 dst) => bits switch
		{
			ShaderStageFlags.GLS_SRCBLEND_ZERO => Vector3.Zero,
			ShaderStageFlags.GLS_SRCBLEND_ONE => Vector3.One,
			ShaderStageFlags.GLS_SRCBLEND_DST_COLOR => dst,
			ShaderStageFlags.GLS_SRCBLEND_ONE_MINUS_DST_COLOR => Vector3.One - dst,
			ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA => new Vector3(a),
			ShaderStageFlags.GLS_SRCBLEND_ONE_MINUS_SRC_ALPHA => new Vector3(1f - a),
			ShaderStageFlags.GLS_SRCBLEND_DST_ALPHA => Vector3.One,        // opaque framebuffer => dst alpha 1
			ShaderStageFlags.GLS_SRCBLEND_ONE_MINUS_DST_ALPHA => Vector3.Zero,
			_ => Vector3.One
		};

		private static Vector3 DstFactor(ShaderStageFlags bits, Vector3 src, float a, Vector3 dst) => bits switch
		{
			ShaderStageFlags.GLS_DSTBLEND_ZERO => Vector3.Zero,
			ShaderStageFlags.GLS_DSTBLEND_ONE => Vector3.One,
			ShaderStageFlags.GLS_DSTBLEND_SRC_COLOR => src,
			ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_COLOR => Vector3.One - src,
			ShaderStageFlags.GLS_DSTBLEND_SRC_ALPHA => new Vector3(a),
			ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA => new Vector3(1f - a),
			ShaderStageFlags.GLS_DSTBLEND_DST_ALPHA => Vector3.One,
			ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_DST_ALPHA => Vector3.Zero,
			_ => Vector3.Zero
		};

		// Bilinear sample at texcoord st, wrapping or clamping per the stage's clampMap flag.
		private static Vector4 SampleTexel(Rgba32[] pixels, int w, int h, Vector2 st, bool clamp)
		{
			if (clamp)
				st = Vector2.Clamp(st, Vector2.Zero, Vector2.One);

			var fx = WrapOrClampCoord(st.X, clamp) * w - 0.5f;
			var fy = WrapOrClampCoord(st.Y, clamp) * h - 0.5f;

			var x0 = (int)MathF.Floor(fx);
			var y0 = (int)MathF.Floor(fy);
			var dx = fx - x0;
			var dy = fy - y0;

			int X0, Y0, X1, Y1;
			if (clamp)
			{
				X0 = Math.Clamp(x0, 0, w - 1);
				Y0 = Math.Clamp(y0, 0, h - 1);
				X1 = Math.Clamp(x0 + 1, 0, w - 1);
				Y1 = Math.Clamp(y0 + 1, 0, h - 1);
			}
			else
			{
				X0 = ((x0 % w) + w) % w;
				Y0 = ((y0 % h) + h) % h;
				X1 = (X0 + 1) % w;
				Y1 = (Y0 + 1) % h;
			}

			var c00 = ToVector(pixels[Y0 * w + X0]);
			var c10 = ToVector(pixels[Y0 * w + X1]);
			var c01 = ToVector(pixels[Y1 * w + X0]);
			var c11 = ToVector(pixels[Y1 * w + X1]);

			return Vector4.Lerp(Vector4.Lerp(c00, c10, dx), Vector4.Lerp(c01, c11, dx), dy);
		}

		private static float WrapOrClampCoord(float v, bool clamp) => clamp ? Math.Clamp(v, 0f, 1f) : v - MathF.Floor(v);

		private static bool IsAdditiveBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE &&
				(srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE || srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA);
		}

		// A transparent overlay blend - additive ("GL_one GL_one") or alpha ("GL_src_alpha GL_one_minus_src_alpha").
		// A stage that's neither is an opaque base (e.g. a plain map, or a "GL_dst_color" lightmap multiply).
		private static bool IsOverlayBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			var isAlphaBlend = srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA &&
				dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
			return IsAdditiveBlend(stage) || isAlphaBlend;
		}

		private void WriteAnimMapVmt(string textureName, Shader shader, int fps, bool isAdditive)
		{
			var shaderType = shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP) ? "UnlitGeneric" : "LightmappedGeneric";

			var sb = new StringBuilder();
			sb.AppendLine(shaderType);
			sb.AppendLine("{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$basetexture \"{textureName}\"");
			if (isAdditive)
				sb.AppendLine("\t$additive 1");
			if (shader.cullType == CullType.TWO_SIDED)
				sb.AppendLine("\t$nocull 1");
			sb.AppendLine("\tProxies");
			sb.AppendLine("\t{");
			sb.AppendLine("\t\tAnimatedTexture");
			sb.AppendLine("\t\t{");
			sb.AppendLine("\t\t\tanimatedTextureVar $basetexture");
			sb.AppendLine("\t\t\tanimatedTextureFrameNumVar $frame");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tanimatedTextureFrameRate {fps}");
			sb.AppendLine("\t\t}");
			sb.AppendLine("\t}");
			sb.AppendLine("}");

			var vmtPath = Path.Combine(pk3Dir, textureName + ".vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath)!);
			File.WriteAllText(vmtPath, sb.ToString());
		}

		private List<Layer>? LoadLayers(Shader shader, out float maxRate)
		{
			var stages = GetTextureStages(shader);

			// The fastest scroll component sets the loop's pace; every layer's per-loop travel is scaled
			// against it and snapped to a whole number of tiles so the baked loop is seamless. maxRate (the
			// real Q3 scroll rate, in tiles/sec) is also handed back to drive the auto playback speed.
			maxRate = stages
				.Select(GetScroll)
				.SelectMany(v => new[] { Math.Abs(v.X), Math.Abs(v.Y) })
				.DefaultIfEmpty(0f)
				.Max();
			if (maxRate <= 0f)
				return null;

			var layers = new List<Layer>();
			foreach (var stage in stages)
			{
				var imagePath = resolveImagePath(Path.ChangeExtension(stage.bundles[0].images[0], null));
				if (imagePath == null || !File.Exists(imagePath))
					return null; // can't composite without every layer - fall back to the normal path

				using var image = Image.Load<Rgba32>(imagePath);
				var pixels = new Rgba32[image.Width * image.Height];
				image.CopyPixelDataTo(pixels);

				var scroll = GetScroll(stage);
				layers.Add(new Layer
				{
					pixels = pixels,
					width = image.Width,
					height = image.Height,
					hasAlpha = pixels.Any(p => p.A < 255),
					scale = GetScale(stage),
					scrollTiles = new Vector2(
						MathF.Round(scroll.X / maxRate * options.maxLoopTiles),
						MathF.Round(scroll.Y / maxRate * options.maxLoopTiles))
				});
			}

			return layers;
		}

		private static Vector2 GetScroll(ShaderStage stage)
		{
			var scroll = Vector2.Zero;
			foreach (var texMod in stage.bundles[0].texMods)
			{
				if (texMod.type == TexMod.TMOD_SCROLL)
					scroll += new Vector2(texMod.scroll[0], texMod.scroll[1]);
			}
			return scroll;
		}

		// Combined tcmod scale magnitude for a layer (sign/flip is dropped - it only mirrors the pattern).
		private static Vector2 GetScale(ShaderStage stage)
		{
			var scale = Vector2.One;
			foreach (var texMod in stage.bundles[0].texMods)
			{
				if (texMod.type == TexMod.TMOD_SCALE)
					scale *= new Vector2(texMod.scale[0], texMod.scale[1]);
			}
			return new Vector2(MathF.Abs(scale.X), MathF.Abs(scale.Y));
		}

		private List<byte[]> BakeFrames(List<Layer> layers)
		{
			var size = options.resolution;
			var frames = new List<byte[]>(options.frames);

			// Drive per-texel alpha from the source alpha channel when one exists, otherwise from luminance.
			var useSourceAlpha = layers.Any(l => l.hasAlpha);

			for (var f = 0; f < options.frames; f++)
			{
				var phase = (float)f / options.frames; // 0..1 across the loop; phase 1 == phase 0 -> seamless
				var frame = new byte[size * size * 4];

				for (var y = 0; y < size; y++)
				{
					for (var x = 0; x < size; x++)
					{
						var uv = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);

						// Composite every layer symmetrically with a screen blend (1-(1-a)(1-b)). The Q3 layers
						// each brighten the framebuffer ("GL_dst_color GL_one"); screening keeps all of them
						// visible at once - so the different scroll directions read as distinct ripple sets -
						// while staying bounded in [0,1] (unlike a raw additive accumulation). Alpha (when the
						// sources have one) is screened the same way.
						var color = Vector3.Zero;
						var srcAlpha = 0f;
						foreach (var layer in layers)
						{
							var sample = SampleLayer(layer, uv, phase);
							var rgb = new Vector3(sample.X, sample.Y, sample.Z);
							color = Vector3.One - (Vector3.One - color) * (Vector3.One - rgb);
							srcAlpha = 1f - (1f - srcAlpha) * (1f - sample.W);
						}

						color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);

						float alpha;
						if (options.autoAlpha)
						{
							// Luminance (Rec.601) gives "bright caustics opaque, dark gaps transparent".
							var basis = useSourceAlpha ? srcAlpha : (0.299f * color.X + 0.587f * color.Y + 0.114f * color.Z);
							alpha = Math.Clamp(basis * options.alpha, 0f, 1f);
						}
						else
						{
							alpha = 1f; // opaque texels; translucency (if any) comes from the VMT's constant $alpha
						}

						var index = (y * size + x) * 4;
						frame[index + 0] = (byte)(color.X * 255f + 0.5f);
						frame[index + 1] = (byte)(color.Y * 255f + 0.5f);
						frame[index + 2] = (byte)(color.Z * 255f + 0.5f);
						frame[index + 3] = (byte)(alpha * 255f + 0.5f);
					}
				}

				frames.Add(frame);
			}

			return frames;
		}

		// Samples one full, seamless source tile for the layer, scrolled by its whole-tile travel * phase.
		// Baking exactly one tile (rather than a fractional "tcmod scale" crop) is what keeps each flipbook
		// repeat seamless; the scale itself is reapplied on the surface via the VMT. Whole-tile scroll * phase
		// (instead of rate * time) makes the last frame line up with the first. TMOD_TRANSFORM/ROTATE/TURB/
		// STRETCH (shear and animated wobble) aren't reproduced yet.
		private Vector4 SampleLayer(Layer layer, Vector2 uv, float phase)
		{
			return SampleBilinearWrap(layer, uv + layer.scrollTiles * phase);
		}

		private static Vector4 SampleBilinearWrap(Layer layer, Vector2 st)
		{
			var w = layer.width;
			var h = layer.height;

			var fx = (st.X - MathF.Floor(st.X)) * w - 0.5f;
			var fy = (st.Y - MathF.Floor(st.Y)) * h - 0.5f;

			var x0 = (int)MathF.Floor(fx);
			var y0 = (int)MathF.Floor(fy);
			var dx = fx - x0;
			var dy = fy - y0;

			var x0w = ((x0 % w) + w) % w;
			var y0w = ((y0 % h) + h) % h;
			var x1w = (x0w + 1) % w;
			var y1w = (y0w + 1) % h;

			var c00 = ToVector(layer.pixels[y0w * w + x0w]);
			var c10 = ToVector(layer.pixels[y0w * w + x1w]);
			var c01 = ToVector(layer.pixels[y1w * w + x0w]);
			var c11 = ToVector(layer.pixels[y1w * w + x1w]);

			return Vector4.Lerp(Vector4.Lerp(c00, c10, dx), Vector4.Lerp(c01, c11, dx), dy);
		}

		private static Vector4 ToVector(Rgba32 pixel) => new(pixel.R / 255f, pixel.G / 255f, pixel.B / 255f, pixel.A / 255f);

		private bool BakeVtf(string vtfPath, List<byte[]> frames, int width, int height, ImageFormat format)
		{
			var w = (ushort)width;
			var h = (ushort)height;

			using var vtf = new VTF();
			vtf.Version = 6;

			// Establish format/size from frame 0, grow to the full frame count, then fill the rest.
			if (!vtf.SetImage(frames[0], ImageFormat.RGBA8888, w, h))
				return false;
			if (!vtf.SetFrameCount((ushort)frames.Count))
				return false;
			for (var i = 1; i < frames.Count; i++)
			{
				if (!vtf.SetImage(frames[i], ImageFormat.RGBA8888, w, h, frame: (ushort)i))
					return false;
			}

			// Mirror sourcepp's createInternal: reflectivity + mips computed in the source format, then compress.
			vtf.ComputeReflectivity();
			vtf.SetRecommendedMipCount();
			vtf.ComputeMips();
			vtf.SetFormat(format);
			vtf.ComputeTransparencyFlags();

			return vtf.Bake(vtfPath);
		}

		// Picks a compact format. 256px uses BC7 (full alpha, higher quality). 128px uses DXT1 (0.5 byte/px)
		// when alpha isn't needed, or DXT5 (1 byte/px) when autoAlpha bakes a per-texel alpha channel (DXT1's
		// 1-bit alpha can't hold a smooth gradient).
		private ImageFormat GetOutputFormat()
		{
			if (options.resolution >= 256)
				return ImageFormat.STRATA_BC7;

			return options.autoAlpha ? ImageFormat.DXT5 : ImageFormat.DXT1;
		}

		private void WriteVmt(string textureName, Vector2 scale, int fps)
		{
			// Q3 liquids are surfaceparm trans (see-through). $translucent puts the surface on the translucent
			// render/sort path. With autoAlpha the per-texel alpha baked into the VTF drives translucency;
			// otherwise a flat constant $alpha does (needs no texture alpha channel, so DXT1 is fine).
			var alpha = Math.Clamp(options.alpha, 0f, 1f);
			string alphaLine;
			if (options.autoAlpha)
				alphaLine = "\n\t$translucent 1";
			else if (alpha < 1f)
				alphaLine = $"\n\t$translucent 1\n\t$alpha {alpha.ToString(CultureInfo.InvariantCulture)}";
			else
				alphaLine = string.Empty;

			// Reapply the shader's tcmod scale on the surface (the flipbook itself is baked at scale 1). A
			// scale < 1 enlarges the texture / reduces visible tiling, matching Q3's "tcmod scale".
			var transformLine = (MathF.Abs(scale.X - 1f) > 0.01f || MathF.Abs(scale.Y - 1f) > 0.01f)
				? $"\n\t$basetexturetransform \"center .5 .5 scale {scale.X.ToString(CultureInfo.InvariantCulture)} {scale.Y.ToString(CultureInfo.InvariantCulture)} rotate 0 translate 0 0\""
				: string.Empty;

			var vmt = $$"""
				LightmappedGeneric
				{
					$basetexture "{{textureName}}"{{alphaLine}}{{transformLine}}
					Proxies
					{
						AnimatedTexture
						{
							animatedTextureVar $basetexture
							animatedTextureFrameNumVar $frame
							animatedTextureFrameRate {{fps.ToString(CultureInfo.InvariantCulture)}}
						}
					}
				}
				""";

			var vmtPath = Path.Combine(pk3Dir, textureName + ".vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath)!);
			File.WriteAllText(vmtPath, vmt);
		}
	}
}
