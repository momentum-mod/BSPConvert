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
using static BSPConvert.Lib.ShaderStageUtils;

namespace BSPConvert.Lib
{
	// Tunables for baking Q3 multi-pass / animated shaders into looping flipbook VTFs. File size is governed by
	// a single size budget: the baker picks the frame count from the animation's seamless loop length, then sizes
	// the resolution to fit the budget (more frames -> lower resolution), bounded by maxResolution. The CLI maps
	// its --anim* flags straight onto these.
	public class FlipbookOptions
	{
		public bool enabled = true;

		// Target maximum size (in bytes) of each baked animated VTF. The master knob: resolution adapts down as
		// the frame count rises so total bytes (frames * resolution^2 * bytesPerTexel) stays near this. Mips add
		// ~33% on top, so it's an approximate target rather than a hard ceiling.
		public long byteBudget = 16L * 1024 * 1024;

		// Sharpness ceiling - the bake never exceeds this per-axis resolution even when the budget would allow it
		// (and never upscales past the source/tiling detail either). Effectively the "max resolution" override.
		public int maxResolution = 512;

		// Playback fps of the AnimatedTexture proxy, i.e. smoothness. The seamless loop is baked at this rate;
		// when the loop needs more frames than maxFrames it is compressed into the budget (plays faster) rather
		// than dropped below this fps.
		public int fps = 16;

		// Hard cap on baked frame count (bounds file size and stays within the VTF frame limit). When the loop's
		// true length exceeds this at the chosen fps, the animation is sped up to fit while staying seamless.
		public int maxFrames = 512;

		// Translucency for the brighten/water output mode. 1 (default) derives per-texel alpha from each source's
		// alpha/luminance (the "GL_dst_color" brightening look); below 1 uses a flat constant alpha instead.
		public float alpha = 1.0f;
		public bool autoAlpha = true;
	}

	// Bakes Q3 multi-pass / animated shaders that no single Source material can reproduce into one looping
	// flipbook VTF plus an AnimatedTexture VMT. A unified compositor replays the shader's layers per pixel per
	// frame; the output mode (opaque self-contained surface, additive glow, or translucent brightening liquid)
	// is chosen from the blend stack. animMap frame sequences (e.g. fire) keep their own frame-exact path.
	public class FlipbookConverter
	{
		private const int MinResolution = 16;

		private readonly string pk3Dir;
		private readonly FlipbookOptions options;
		private readonly Func<string, string?> resolveImagePath;
		private readonly Action<string> copyExternalContent;
		private readonly bool noEnvMap;

		public FlipbookConverter(string pk3Dir, FlipbookOptions options, Func<string, string?> resolveImagePath, Action<string> copyExternalContent, bool noEnvMap = false)
		{
			this.pk3Dir = pk3Dir;
			this.options = options;
			this.resolveImagePath = resolveImagePath;
			this.copyExternalContent = copyExternalContent;
			this.noEnvMap = noEnvMap;
		}

		// One stage of the blend stack: its frame image(s) plus everything needed to replay it over time.
		private class StackLayer
		{
			public Rgba32[][] frames = Array.Empty<Rgba32[]>(); // one buffer per animMap frame (>=1)
			public int width;
			public int height;
			public bool hasAlpha;                     // any source texel has a non-opaque alpha channel
			public float animSpeed;                   // animMap fps (0 => single static image)
			public bool clamp;                        // clampMap => clamp texcoords instead of wrapping
			public List<TexModInfo> texMods = new();
			public ColorGen rgbGen;
			public WaveForm rgbWave = new();
			public AlphaGen alphaGen;
			public WaveForm alphaWave = new();
			public byte[] constantColor = new byte[4];
			public ShaderStageFlags flags;            // src/dst blend factors
		}

		// Bakes the flipbook + writes the VMT if the shader matches an animated multi-pass pattern. Returns false
		// (leaving the shader for the normal material path) when disabled, unmatched, or a source image is missing.
		public bool TryConvert(string textureName, Shader shader)
		{
			if (!options.enabled)
				return false;

			// animMap shaders are explicit frame sequences (e.g. fire) baked frame-exact at native resolution;
			// complex animMap stacks delegate into the unified compositor from there.
			if (IsAnimMapShader(shader))
				return ConvertAnimMap(textureName, shader);

			// Any other multi-pass animated surface - scrolling liquids, scrolling/rotating/pulsing overlays on an
			// opaque base, stacked additive glows - is composited stage by stage into a looping flipbook. (Scroll-only
			// liquids are handled live by DetailMaterialConverter, which runs ahead of this in MaterialConverter.)
			if (IsAnimatedStackShader(shader, out var stages, out var mode))
				return BakeAnimatedStack(textureName, shader, stages, mode);

			// Q3 chrome carrying an animated overlay (e.g. a pulsing glow) over a spheremap reflection: the
			// reverse-alpha diffuse base isn't an opaque GL_one GL_zero blend, so IsAnimatedStackShader doesn't
			// claim it. Bake the diffuse+overlay stack (Opaque) and add the view-dependent reflection live via
			// $spheremap - MaterialConverter would otherwise keep the reflection but drop the animation.
			if (IsReflectiveAnimatedStack(shader, out var reflStages, out var envStage))
				return BakeAnimatedStack(textureName, shader, reflStages, OutputMode.Opaque, envStage);

			return false;
		}

		// Matches a "tcGen environment" reflection sitting beneath an animated diffuse stack (>= 2 visible stages,
		// at least one reproducibly animated) with a non-overlay diffuse base - the Q3 blue-metal chrome idiom.
		// The reflection is added live ($spheremap) atop the baked animation rather than baked away. Env maps off
		// => no reflection, so it's left for the normal path. Runs after IsAnimatedStackShader, so it only sees
		// stacks that matcher rejected (its opaque base is GL_one GL_zero, which a reverse-alpha base is not).
		private bool IsReflectiveAnimatedStack(Shader shader, out List<ShaderStage> stages, out ShaderStage? envStage)
		{
			stages = GetTextureStages(shader);
			envStage = noEnvMap ? null : shader.GetImageStages()
				.FirstOrDefault(s => s.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED && IsOpaqueBlend(s));

			return envStage != null && stages.Count >= 2 && AnimatesReproducibly(stages) &&
				stages.Any(s => !IsOverlayBlend(s));
		}

		// Matches shaders with an animMap stage - an explicit frame sequence (Q3 "animMap <fps> f1 f2 ...").
		private bool IsAnimMapShader(Shader shader)
		{
			return GetTextureStages(shader).Any(s => s.bundles[0].numImageAnimations > 1);
		}

		// Bakes a Q3 animMap (explicit frame sequence, e.g. a fire effect) into a flipbook VTF - one VTF frame per
		// animMap frame, played at the animMap's own frequency. Each animMap stage advances through its own frame
		// list; plain "map" stages stay constant. Additive shaders (the common sfx case, "GL_one GL_one") sum every
		// stage per frame; otherwise the first animMap stage is used as an opaque animated base.
		private bool ConvertAnimMap(string textureName, Shader shader)
		{
			var stages = GetTextureStages(shader);
			var animStages = stages.Where(s => s.bundles[0].numImageAnimations > 1).ToList();
			if (animStages.Count == 0)
				return false;

			// A non-additive overlay (a multiply/filter mask or an alpha layer) can't be reproduced by the simple
			// additive-sum below. That's a complex layered effect (e.g. masked ring animations over a decal) - hand
			// it to the unified compositor, which replays each stage's real blendFunc, animMap, tcMod and waveforms.
			if (stages.Skip(1).Any(s => !IsAdditiveBlend(s)))
				return BakeAnimatedStack(textureName, shader, stages, ClassifyOutputMode(shader, stages));

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
			// first frame). Any mismatched frame is resized to match (frames in one VTF must share dimensions).
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

			WriteStackVmt(textureName, shader, fps, isAdditive ? OutputMode.Additive : OutputMode.Opaque, null);
			return true;
		}

		// The unified bake: replays a multi-pass shader's blend stack per pixel per frame into one seamless looping
		// flipbook. Shared by liquids (Brighten), opaque-base overlays (Opaque) and stacked glows (Additive); the
		// per-pixel composite differs only in how the layers combine and how alpha is produced.
		private bool BakeAnimatedStack(string textureName, Shader shader, List<ShaderStage> stages, OutputMode mode, ShaderStage? envStage = null)
		{
			var layers = new List<StackLayer>();
			int srcW = 0, srcH = 0;
			foreach (var stage in stages)
			{
				var bundle = stage.bundles[0];
				var count = bundle.numImageAnimations > 1 ? bundle.numImageAnimations : 1;
				var buffers = new Rgba32[count][];
				var hasAlpha = false;
				int lw = 0, lh = 0;
				for (var i = 0; i < count; i++)
				{
					var imagePath = resolveImagePath(Path.ChangeExtension(bundle.images[i], null));
					if (imagePath == null || !File.Exists(imagePath))
						return false; // can't composite without every layer - fall back to the normal path

					using var image = Image.Load<Rgba32>(imagePath);
					if (lw == 0) { lw = image.Width; lh = image.Height; }
					if (image.Width != lw || image.Height != lh)
						image.Mutate(x => x.Resize(lw, lh)); // frames of one stage must share size

					var pixels = new Rgba32[lw * lh];
					image.CopyPixelDataTo(pixels);
					if (!hasAlpha)
						hasAlpha = pixels.Any(p => p.A < 255);
					buffers[i] = pixels;
				}

				layers.Add(new StackLayer
				{
					frames = buffers,
					width = lw,
					height = lh,
					hasAlpha = hasAlpha,
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

				srcW = Math.Max(srcW, lw);
				srcH = Math.Max(srcH, lh);
			}

			if (srcW == 0 || srcH == 0)
				return false;

			// For reflective chrome, the reverse-alpha diffuse base's alpha masks how much of the live
			// $spheremap reflection shows through. It's baked into the flipbook's alpha as (1 - alpha) so the
			// VMT's $basealphaenvmapmask (reflection *= 1 - bakedAlpha) reproduces the Q3 refl*alpha weighting.
			// Track the source layer so it survives DropOutlierLayers and the AdjustLayer copy below. (layers is
			// built 1:1 with stages, so the reverse-alpha stage's index selects its layer.)
			var maskIndex = envStage == null ? -1 : stages.FindIndex(IsReverseAlphaBlend);
			var maskLayerSrc = maskIndex >= 0 ? layers[maskIndex] : null;

			// Anchor the bake to the lowest-frequency (coarsest) layer: the bake tile spans exactly one of its tiles,
			// so every other layer tiles a whole number of times within it and keeps its true Q3 scale, tiling
			// seamlessly. That coarsest layer's scale is reapplied on the surface via $basetexturetransform. To stop a
			// very coarse overlay (e.g. slime's big bubbles) from forcing the finer layers to tile so many times they
			// blur at the resolution cap, outlier-coarse layers are dropped first (never the opaque base; see
			// DropOutlierLayers).
			var layerScales = layers.Select(l => GetScaleFromTexMods(l.texMods)).ToList();
			DropOutlierLayers(layers, layerScales, options.maxResolution);
			var refIndex = ChooseReferenceLayer(layers, layerScales);
			var surfaceScale = new Vector2(
				MathF.Max(layerScales[refIndex].X, 1e-4f),
				MathF.Max(layerScales[refIndex].Y, 1e-4f));
			var tileCounts = new List<Vector2>(layers.Count);
			for (var i = 0; i < layers.Count; i++)
			{
				// Clamped layers can't wrap, so they stay at one tile.
				var tx = layers[i].clamp ? 1f : MathF.Max(1f, MathF.Round(layerScales[i].X / surfaceScale.X));
				var ty = layers[i].clamp ? 1f : MathF.Max(1f, MathF.Round(layerScales[i].Y / surfaceScale.Y));
				tileCounts.Add(new Vector2(tx, ty));
			}

			// Loop length. The seamless period is 1/gcd(rates); if it fits the frame budget (maxFrames at fps) it's
			// baked exactly at true speed. If it's longer, the loop is bounded to the budget and each layer's rate is
			// snapped to a whole number of cycles over it (a small per-layer speed adjustment) - a short, seamless
			// loop that plays near Q3's speed, rather than compressing a huge period into the budget (too fast).
			var fps = Math.Clamp(options.fps, 1, 60);
			var maxFrames = Math.Clamp(options.maxFrames, 1, 4096);
			var trueLoop = ComputeTrueLoopSeconds(layers);
			var budgetLoop = (float)maxFrames / fps;
			var snapRates = trueLoop > budgetLoop;
			var loopSeconds = trueLoop <= 0f ? 0f : (snapRates ? budgetLoop : trueLoop);
			var frameCount = loopSeconds <= 0f
				? 1
				: (snapRates ? maxFrames : Math.Clamp((int)MathF.Round(loopSeconds * fps), 2, maxFrames));

			// Fold each layer's snapped tiling (and snapped rates, when bounding the loop) into a bake-ready copy, so
			// the composite samples directly at uv and stays seamless in both space and time.
			var bakeLayers = new List<StackLayer>(layers.Count);
			for (var i = 0; i < layers.Count; i++)
				bakeLayers.Add(AdjustLayer(layers[i], tileCounts[i], snapRates, loopSeconds));

			// The reflection-mask layer's bake-ready copy (unless DropOutlierLayers removed it). bakeLayers is
			// built 1:1 with the post-drop layers, so its position in layers selects the adjusted copy.
			var maskPos = maskLayerSrc == null ? -1 : layers.IndexOf(maskLayerSrc);
			var maskLayer = maskPos >= 0 ? bakeLayers[maskPos] : null;

			// Ideal bake size: a layer tiling N times wants N * source pixels to stay sharp; take the largest demand.
			int idealW = 0, idealH = 0;
			for (var i = 0; i < layers.Count; i++)
			{
				idealW = Math.Max(idealW, (int)MathF.Ceiling(layers[i].width * tileCounts[i].X));
				idealH = Math.Max(idealH, (int)MathF.Ceiling(layers[i].height * tileCounts[i].Y));
			}

			// The reflection mask also needs 8-bit alpha, so an alpha-capable (BC7) format - DXT1 would drop it.
			var alphaNeeded = (mode == OutputMode.Brighten && options.autoAlpha) || maskLayer != null;
			var bytesPerTexel = alphaNeeded ? 1.0f : 0.5f; // BC7 (alpha) vs DXT1 (no alpha)
			var (bakeW, bakeH) = SolveResolution(idealW, idealH, frameCount, bytesPerTexel);

			var useSourceAlpha = layers.Any(l => l.hasAlpha);
			var pixelCount = bakeW * bakeH;
			var frames = new List<byte[]>(frameCount);

			for (var f = 0; f < frameCount; f++)
			{
				// Spread frames evenly across one loop period so the last leads straight back into the first.
				var t = frameCount > 0 ? (float)f / frameCount * loopSeconds : 0f;
				var frame = new byte[pixelCount * 4];
				for (var y = 0; y < bakeH; y++)
				{
					for (var x = 0; x < bakeW; x++)
					{
						var uv = new Vector2((x + 0.5f) / bakeW, (y + 0.5f) / bakeH);
						var (rgb, alpha) = CompositePixel(bakeLayers, uv, t, mode, useSourceAlpha, maskLayer);

						var index = (y * bakeW + x) * 4;
						frame[index + 0] = (byte)(rgb.X * 255f + 0.5f);
						frame[index + 1] = (byte)(rgb.Y * 255f + 0.5f);
						frame[index + 2] = (byte)(rgb.Z * 255f + 0.5f);
						frame[index + 3] = (byte)(alpha * 255f + 0.5f);
					}
				}
				frames.Add(frame);
			}

			var vtfPath = Path.Combine(pk3Dir, textureName + ".vtf");
			Directory.CreateDirectory(Path.GetDirectoryName(vtfPath)!);
			var format = alphaNeeded ? ImageFormat.STRATA_BC7 : ImageFormat.DXT1;
			if (!BakeVtf(vtfPath, frames, bakeW, bakeH, format))
				return false;

			if (envStage != null) // ensure the reflection texture reaches a VTF for $spheremap
				copyExternalContent(Path.ChangeExtension(envStage.bundles[0].images[0], null));

			var applyScale = MathF.Abs(surfaceScale.X - 1f) > 0.01f || MathF.Abs(surfaceScale.Y - 1f) > 0.01f;
			WriteStackVmt(textureName, shader, fps, mode, applyScale ? surfaceScale : null, envStage, maskLayer != null);
			return true;
		}

		// The reference layer kept at exactly one tile: the lowest-frequency (coarsest) layer. Anchoring the bake to
		// it lets every other (finer) layer tile a whole number of times within the bake while keeping its true Q3
		// scale. Outlier-coarse layers are dropped beforehand (DropOutlierLayers) so the remaining spread stays sharp.
		private static int ChooseReferenceLayer(List<StackLayer> layers, List<Vector2> scales)
		{
			var refIdx = 0;
			var lowest = float.MaxValue;
			for (var i = 0; i < layers.Count; i++)
			{
				var freq = LayerFrequency(scales[i]);
				if (freq < lowest)
				{
					lowest = freq;
					refIdx = i;
				}
			}
			return refIdx;
		}

		// Scalar spatial frequency of a layer (geometric mean of its per-axis tcMod scale): higher = the texture
		// repeats more often across the surface (finer detail), lower = magnified / coarser.
		private static float LayerFrequency(Vector2 scale)
		{
			return MathF.Sqrt(MathF.Max(scale.X, 1e-4f) * MathF.Max(scale.Y, 1e-4f));
		}

		// Drops overlay layers whose spatial frequency is far below the rest of the stack. The bake is anchored to
		// the coarsest layer to preserve every layer's true scale (see BakeAnimatedStack); but if one layer is much
		// coarser than the others, that anchor forces the finer layers to tile so many times they blur at the
		// resolution cap. So outlier-coarse layers are discarded - never the opaque base (the dominant visual), and
		// never below two layers - until the finest layer tiles no more than maxRes/finestSource times over the
		// coarsest remaining layer (i.e. it can still be baked at roughly full source resolution).
		private static void DropOutlierLayers(List<StackLayer> layers, List<Vector2> scales, int maxResolution)
		{
			while (layers.Count > 2)
			{
				int lowIdx = 0, highIdx = 0;
				for (var i = 1; i < layers.Count; i++)
				{
					if (LayerFrequency(scales[i]) < LayerFrequency(scales[lowIdx])) lowIdx = i;
					if (LayerFrequency(scales[i]) > LayerFrequency(scales[highIdx])) highIdx = i;
				}

				// How many times the finest layer must tile if the coarsest layer is the reference.
				var spread = LayerFrequency(scales[highIdx]) / LayerFrequency(scales[lowIdx]);
				var finestDim = Math.Max(layers[highIdx].width, layers[highIdx].height);
				var maxTiles = Math.Max(2f, (float)maxResolution / Math.Max(1, finestDim));
				if (spread <= maxTiles)
					break;

				// Never drop the opaque base; if it's the coarsest layer, accept the softer finer layers instead.
				if (IsOpaqueBlendFlags(layers[lowIdx].flags))
					break;

				layers.RemoveAt(lowIdx);
				scales.RemoveAt(lowIdx);
			}
		}

		// Composites one output texel from every layer at surface coordinate surfUV and loop time t. Opaque/Additive
		// replay the real Q3 blend stack onto a black framebuffer (opaque base overwrites; additives sum). Brighten
		// (Q3 liquids that brighten the scene behind them) screen-composites the layers into a standalone translucent
		// texel, since their real "GL_dst_color" blend depends on the live framebuffer and can't be baked.
		private (Vector3 rgb, float alpha) CompositePixel(List<StackLayer> layers, Vector2 surfUV, float t, OutputMode mode, bool useSourceAlpha, StackLayer? maskLayer = null)
		{
			if (mode == OutputMode.Brighten)
			{
				var color = Vector3.Zero;
				var srcAlpha = 0f;
				foreach (var layer in layers)
				{
					var sample = SampleStackLayer(layer, surfUV, t);
					var rgb = new Vector3(sample.X, sample.Y, sample.Z) * EvalRgb(layer, t);
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
				return (color, alpha);
			}

			// Opaque / Additive: replay the ordered blend stack onto the framebuffer (starts black; an opaque base
			// stage overwrites it, then each overlay composites with its real blendFunc).
			var fb = Vector3.Zero;
			var maskAlpha = 1f;
			foreach (var layer in layers)
			{
				var sample = SampleStackLayer(layer, surfUV, t);
				var src = new Vector3(sample.X, sample.Y, sample.Z) * EvalRgb(layer, t);
				var srcA = EvalAlpha(layer, sample.W, t);
				// Reflection mask: bake (1 - diffuseAlpha) so $basealphaenvmapmask yields refl*alpha (see BakeAnimatedStack).
				if (layer == maskLayer)
					maskAlpha = 1f - srcA;
				fb = BlendStage(layer.flags, src, srcA, fb);
				fb = Vector3.Clamp(fb, Vector3.Zero, Vector3.One); // Q3's 8-bit framebuffer clamps each pass
			}
			return (fb, maskLayer != null ? maskAlpha : 1f); // else opaque base bakes opaque; additive ignores alpha ($additive)
		}

		// Samples a layer's current animMap frame at loop time t, after applying its tcmods to the surface coord.
		private static Vector4 SampleStackLayer(StackLayer layer, Vector2 surfUV, float t)
		{
			var animIdx = layer.animSpeed > 0f && layer.frames.Length > 1
				? (int)MathF.Floor(t * layer.animSpeed) % layer.frames.Length
				: 0;
			var st = TransformTexcoord(surfUV, layer.texMods, t);
			return SampleTexel(layer.frames[animIdx], layer.width, layer.height, st, layer.clamp);
		}

		// Bake resolution, adapted to the size budget for a given frame count. Total texels across all frames must
		// fit byteBudget/bytesPerTexel, so more frames => lower resolution. Bounded by maxResolution and never
		// upscaled past the ideal (source/tiling) detail.
		private (int bakeW, int bakeH) SolveResolution(int idealW, int idealH, int frameCount, float bytesPerTexel)
		{
			var maxRes = Math.Clamp(options.maxResolution, MinResolution, 2048);
			float w = Math.Min(Math.Max(idealW, 1), maxRes);
			float h = Math.Min(Math.Max(idealH, 1), maxRes);

			// Shrink (preserving aspect) until one frame fits its share of the texel budget.
			var perFrameTexels = options.byteBudget / (double)bytesPerTexel / Math.Max(1, frameCount);
			if (w * h > perFrameTexels && w * h > 0)
			{
				var s = (float)Math.Sqrt(perFrameTexels / (w * h));
				w *= s;
				h *= s;
			}

			// Source VTFs are power-of-two (sourcepp rounds non-PoT dimensions UP, which would blow past the
			// budget), so snap each axis DOWN to the largest PoT that fits.
			var bakeW = Math.Max(MinResolution, FloorToPowerOfTwo((int)MathF.Round(w)));
			var bakeH = Math.Max(MinResolution, FloorToPowerOfTwo((int)MathF.Round(h)));
			return (bakeW, bakeH);
		}

		private static int FloorToPowerOfTwo(int v)
		{
			if (v < 1)
				return 1;
			var p = 1;
			while (p * 2 <= v)
				p *= 2;
			return p;
		}

		// The true seamless period: each animated element (animMap, scroll, rotate, tcMod stretch, rgb/alpha wave)
		// cycles at its own rate, and the loop wraps cleanly once every one has completed a whole number of cycles,
		// i.e. after 1 / gcd(rates). Returns 0 when nothing animates. May be very large for incommensurate rates -
		// the caller bounds it to the frame budget and snaps rates rather than baking an enormous loop.
		private static float ComputeTrueLoopSeconds(List<StackLayer> layers)
		{
			var rates = GatherRates(layers);
			if (rates.Count == 0)
				return 0f;

			var rateGcd = rates.Aggregate(RateGcd);
			if (rateGcd <= 1e-4f)
				return float.MaxValue; // effectively incommensurate -> treat as infinite so the loop gets bounded
			return 1f / rateGcd;
		}

		// Cycles-per-second of every reproduced periodic element across all layers (one entry per scroll axis, etc.).
		private static List<float> GatherRates(List<StackLayer> layers)
		{
			var rates = new List<float>();
			foreach (var layer in layers)
			{
				if (layer.animSpeed > 0f && layer.frames.Length > 1)
					rates.Add(layer.animSpeed / layer.frames.Length); // full image-sequence cycles per second

				foreach (var texMod in layer.texMods)
				{
					// TMOD_TURBULENT / TMOD_TRANSFORM shear isn't reproduced, so it doesn't constrain the loop.
					if (texMod.type == TexMod.TMOD_STRETCH && texMod.wave.frequency > 0f)
						rates.Add(texMod.wave.frequency);

					// A scroll cycles once per tile travelled (one rate per moving axis); a rotate once per revolution.
					else if (texMod.type == TexMod.TMOD_SCROLL)
					{
						if (MathF.Abs(texMod.scroll[0]) > 1e-4f)
							rates.Add(MathF.Abs(texMod.scroll[0]));
						if (MathF.Abs(texMod.scroll[1]) > 1e-4f)
							rates.Add(MathF.Abs(texMod.scroll[1]));
					}
					else if (texMod.type == TexMod.TMOD_ROTATE && MathF.Abs(texMod.rotateSpeed) > 1e-4f)
						rates.Add(MathF.Abs(texMod.rotateSpeed) / 360f);
				}

				if (layer.rgbGen == ColorGen.CGEN_WAVEFORM && layer.rgbWave.frequency > 0f)
					rates.Add(layer.rgbWave.frequency);
				if (layer.alphaGen == AlphaGen.AGEN_WAVEFORM && layer.alphaWave.frequency > 0f)
					rates.Add(layer.alphaWave.frequency);
			}
			return rates;
		}

		// Builds a bake-ready copy of a layer with its tiling overridden to `tiles` tiles and, when bounding the
		// loop, every periodic rate snapped to a whole number of cycles over loopSeconds (so the loop wraps cleanly).
		private static StackLayer AdjustLayer(StackLayer src, Vector2 tiles, bool snapRates, float loopSeconds)
		{
			return new StackLayer
			{
				frames = src.frames,
				width = src.width,
				height = src.height,
				hasAlpha = src.hasAlpha,
				clamp = src.clamp,
				rgbGen = src.rgbGen,
				alphaGen = src.alphaGen,
				constantColor = src.constantColor,
				flags = src.flags,
				animSpeed = snapRates && src.animSpeed > 0f && src.frames.Length > 1
					? SnapRateMag(src.animSpeed / src.frames.Length, loopSeconds) * src.frames.Length
					: src.animSpeed,
				rgbWave = SnapWaveFreq(src.rgbWave, snapRates && src.rgbGen == ColorGen.CGEN_WAVEFORM, loopSeconds),
				alphaWave = SnapWaveFreq(src.alphaWave, snapRates && src.alphaGen == AlphaGen.AGEN_WAVEFORM, loopSeconds),
				texMods = AdjustTexMods(src.texMods, tiles, snapRates, loopSeconds)
			};
		}

		// A whole number of cycles per loop keeps a rate seamless: round rate*loop to the nearest integer >= 1 (so a
		// layer slower than one cycle per loop still moves rather than freezing), then convert back to a rate.
		private static float SnapRateMag(float rate, float loopSeconds)
		{
			if (rate <= 1e-4f || loopSeconds <= 0f)
				return rate;
			return MathF.Max(1f, MathF.Round(rate * loopSeconds)) / loopSeconds;
		}

		private static float SnapSignedRate(float rate, float loopSeconds) =>
			MathF.CopySign(SnapRateMag(MathF.Abs(rate), loopSeconds), rate);

		private static WaveForm SnapWaveFreq(WaveForm w, bool snap, float loopSeconds)
		{
			var c = new WaveForm { func = w.func, base_ = w.base_, amplitude = w.amplitude, phase = w.phase, frequency = w.frequency };
			if (snap && w.frequency > 1e-4f)
				c.frequency = SnapRateMag(w.frequency, loopSeconds);
			return c;
		}

		// Clones a layer's tcmods, overriding the scale to the snapped tile count and (when snapping) bumping each
		// periodic rate to a whole number of cycles per loop. A layer with no tcmod scale gets one prepended.
		private static List<TexModInfo> AdjustTexMods(List<TexModInfo> src, Vector2 tiles, bool snapRates, float loopSeconds)
		{
			var result = new List<TexModInfo>(src.Count + 1);
			var hasScale = false;
			foreach (var tm in src)
			{
				var c = CloneTexMod(tm);
				switch (tm.type)
				{
					case TexMod.TMOD_SCALE:
						// First scale becomes the tile-count override; any further scales collapse to identity.
						c.scale[0] = hasScale ? 1f : tiles.X;
						c.scale[1] = hasScale ? 1f : tiles.Y;
						hasScale = true;
						break;
					case TexMod.TMOD_SCROLL when snapRates:
						c.scroll[0] = SnapSignedRate(tm.scroll[0], loopSeconds);
						c.scroll[1] = SnapSignedRate(tm.scroll[1], loopSeconds);
						break;
					case TexMod.TMOD_ROTATE when snapRates:
						c.rotateSpeed = SnapSignedRate(tm.rotateSpeed / 360f, loopSeconds) * 360f; // cycle = 360 deg
						break;
					case TexMod.TMOD_STRETCH when snapRates:
						c.wave.frequency = SnapRateMag(tm.wave.frequency, loopSeconds);
						break;
				}
				result.Add(c);
			}
			if (!hasScale && (MathF.Abs(tiles.X - 1f) > 0.01f || MathF.Abs(tiles.Y - 1f) > 0.01f))
			{
				var scale = new TexModInfo { type = TexMod.TMOD_SCALE };
				scale.scale[0] = tiles.X;
				scale.scale[1] = tiles.Y;
				result.Insert(0, scale); // applied before scroll, matching Q3's scale-then-scroll order
			}
			return result;
		}

		private static TexModInfo CloneTexMod(TexModInfo tm)
		{
			var c = new TexModInfo { type = tm.type, rotateSpeed = tm.rotateSpeed };
			c.scale[0] = tm.scale[0];
			c.scale[1] = tm.scale[1];
			c.scroll[0] = tm.scroll[0];
			c.scroll[1] = tm.scroll[1];
			c.wave.func = tm.wave.func;
			c.wave.base_ = tm.wave.base_;
			c.wave.amplitude = tm.wave.amplitude;
			c.wave.phase = tm.wave.phase;
			c.wave.frequency = tm.wave.frequency;
			return c;
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

		// Writes the AnimatedTexture VMT for a baked stack. The output mode drives translucency: Opaque emits a
		// plain lit/unlit surface, Additive emits $additive (black = transparent), Brighten emits $translucent.
		private void WriteStackVmt(string textureName, Shader shader, int fps, OutputMode mode, Vector2? basetextureScale,
			ShaderStage? envStage = null, bool hasReflectionMask = false)
		{
			var shaderType = shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP) ? "UnlitGeneric" : "LightmappedGeneric";

			var sb = new StringBuilder();
			sb.AppendLine(shaderType);
			sb.AppendLine("{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$basetexture \"{textureName}\"");

			// A "tcGen environment" reflection rides live on top of the baked animation via $spheremap.
			if (envStage != null)
				MaterialConverter.AppendSpheremapParameters(sb, envStage, HasLightmapStage(shader), hasReflectionMask);

			// The factored-out tcmod scale (see BakeAnimatedStack) is reapplied here so the surface tiles at Q3's rate.
			if (basetextureScale.HasValue &&
				(MathF.Abs(basetextureScale.Value.X - 1f) > 0.01f || MathF.Abs(basetextureScale.Value.Y - 1f) > 0.01f))
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$basetexturetransform \"center .5 .5 scale {basetextureScale.Value.X} {basetextureScale.Value.Y} rotate 0 translate 0 0\"");

			if (mode == OutputMode.Additive)
			{
				sb.AppendLine("\t$additive 1");
			}
			else if (mode == OutputMode.Brighten)
			{
				sb.AppendLine("\t$translucent 1");
				// Without autoAlpha the VTF carries no per-texel alpha, so a flat constant $alpha drives translucency.
				var alpha = Math.Clamp(options.alpha, 0f, 1f);
				if (!options.autoAlpha && alpha < 1f)
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$alpha {alpha}");
			}

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
	}
}
