using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using sourcepp.vtfpp;

namespace BSPConvert.Lib
{
	// Bakes a Q3 sky shader's cloud stages (scrolling cloud/flare layers projected onto a dome - see
	// textures/skies/hellsky, or breakorp's textures/breaker/breakasky) into a static 6-sided Source skybox.
	// Handles two cases: a "dynamic cloud" sky with no outerbox (skyParms "- 512 -"), where the bake is the
	// sole content of each face, and a sky that has BOTH an outerbox and cloud stages, where the bake layers
	// the clouds on top of the outerbox's own images - matching RB_StageIteratorSky in ioq3 tr_sky.c, which
	// draws the outer box first and then projects every shader stage onto the cloud dome over it. Without
	// this, a shader like breakasky's (a flat outerbox color plus a scrolling cloud+flare overlay) would
	// only convert its flat box, silently losing the cloud/flare layers that make up its actual look.
	//
	// Q3 renders the cloud dome live (see ioq3 tr_sky.c - MakeSkyVec / R_InitSkyTexCoords / FillCloudBox).
	// Source has no equivalent live projection, so the dome projection + stage compositing is reproduced
	// here offline, once per face, and written out as ordinary skybox face textures. Each face is produced
	// exactly as a Q3 "outerbox" .tga for that face would be, so it flows through the same Source skybox
	// path (naming, orientation, worldspawn skyname) as a real image skybox. Scrolling (tcMod scroll) is
	// sampled at time 0 - a static Source sky can't animate it.
	public class CloudSkyboxBaker
	{
		// Output face resolution. Q3 cloud textures are 256; 512 gives the dome projection room to resolve
		// without visible blockiness while staying cheap (6 faces).
		private const int FaceSize = 512;

		// Q3's cloud dome constants (tr_sky.c: R_InitSkyTexCoords).
		private const float RadiusWorld = 4096f;
		private const float DefaultCloudHeight = 512f;

		private readonly string pk3Dir;
		private readonly Func<string, string?> resolveImagePath;

		public CloudSkyboxBaker(string pk3Dir, Func<string, string?> resolveImagePath)
		{
			this.pk3Dir = pk3Dir;
			this.resolveImagePath = resolveImagePath;
		}

		// A bakeable cloud sky: a sky shader (skyParms present) with no outerbox image box but with cloud
		// stages to project. Shared with BSPConverter so the surface's skyname + SURF_SKY routing agrees with
		// what actually gets baked here. (A shader with an outerbox is a real image skybox handled elsewhere;
		// a shader with no skyParms at all is a flat "fake sky" drawn as an ordinary surface.)
		public static bool IsCloudSkyShader(Shader shader)
		{
			return shader?.skyParms != null &&
				!shader.skyParms.HasImageBox &&
				shader.stages != null &&
				shader.GetImageStages().Any();
		}

		// A shader whose non-lightmap stages should be projected onto a baked skybox - either as the sole
		// content of a dome-only sky (IsCloudSkyShader) or layered on top of an outerbox's own images. Used
		// by MaterialConverter to try baking before falling back to the plain outerbox copy (CreateSkyboxVMT),
		// which would otherwise silently drop an outerbox shader's cloud/flare stages.
		public static bool HasBakeableCloudStages(Shader shader)
		{
			return shader?.skyParms != null && shader.stages != null && shader.GetImageStages().Any();
		}

		// The Source skyname (worldspawn) for a cloud sky brush texture, e.g. "textures/skies/hellsky" ->
		// "hellsky". The engine loads materials/skybox/<skyname><suffix>, which is where Bake writes the faces.
		public static string GetSkyName(string textureName)
		{
			var slash = textureName.LastIndexOf('/');
			return slash >= 0 ? textureName.Substring(slash + 1) : textureName;
		}

		// One cloud stage: its source pixels plus how it's projected/blended onto the dome.
		private class Layer
		{
			public Rgba32[] pixels = Array.Empty<Rgba32>();
			public int width;
			public int height;
			public Vector2 scale = Vector2.One;     // combined tcMod scale (texcoords are in ~radians, 0..pi)
			public ShaderStageFlags flags;          // blend mode (opaque base, additive overlay, ...)
			public Vector3 constColor = Vector3.One; // rgbGen const tint, if any
			public bool useConstColor;
		}

		// Bakes the 6 skybox faces + VMTs for a cloud sky shader. Returns false (leaving the shader to the
		// normal material path) only if not a single cloud stage can be loaded.
		//
		// When the shader also has an outerbox (HasImageBox), each face's own outerbox image is loaded as the
		// bake's base layer and written to the same "skybox/{outerBox}{suffix}" path CreateSkyboxVMT would
		// have used, so ResolveSkyboxName/GetSkyName's outerBox-based skyname still resolves to it - baking
		// only changes what MaterialConverter writes at that path, not the name the map spawns with.
		public bool TryConvert(string textureName, Shader shader)
		{
			var layers = LoadLayers(shader);
			if (layers.Count == 0)
				return false;

			var hasOuterBox = shader.skyParms.HasImageBox;
			var cloudHeight = ParseCloudHeight(shader.skyParms.cloudHeight);
			var skyName = hasOuterBox ? shader.skyParms.outerBox : GetSkyName(textureName);

			foreach (var (suffix, q3Face) in Faces)
			{
				var boxFace = hasOuterBox ? LoadBoxFace(shader.skyParms.outerBox, suffix) : null;
				var pixels = BakeFace(q3Face, layers, cloudHeight, boxFace);

				var baseTexture = $"skybox/{skyName}{suffix}";
				if (!BakeFaceVtf(baseTexture, pixels))
					return false;

				WriteSkyboxVmt(baseTexture);
			}

			return true;
		}

		// Loads one outerbox face image (same file the plain image-skybox path uses - see
		// MaterialConverter.CreateSkyboxVMT) as the bake's base layer. Returns null (base starts black) when
		// the image can't be found, rather than failing the whole bake over one missing face.
		private Layer? LoadBoxFace(string outerBox, string suffix)
		{
			var imagePath = resolveImagePath($"{outerBox}_{suffix}");
			if (imagePath == null || !File.Exists(imagePath))
				return null;

			using var image = Image.Load<Rgba32>(imagePath);
			var pixels = new Rgba32[image.Width * image.Height];
			image.CopyPixelDataTo(pixels);

			return new Layer
			{
				pixels = pixels,
				width = image.Width,
				height = image.Height,
			};
		}

		// Source skybox suffix -> Q3 MakeSkyVec face axis. The suffix is NOT the same as the axis index:
		// Q3 draws geometry axis i but samples outerbox image sky_texorder[i] = {0,2,1,3,4,5} (tr_sky.c
		// DrawSkyBox), so the "bk"/"lf" images live on the swapped axes 2/1. Resolving that gives the axis
		// whose view direction matches each Source face (rt=+X, lf=-X, bk=+Y, ft=-Y, up=+Z, dn=-Z), matching
		// the existing image-skybox path that renames Q3's "<name>_<suffix>" to Source's "<name><suffix>".
		private static readonly (string suffix, int q3Face)[] Faces =
		{
			("rt", 0),
			("bk", 2),
			("lf", 1),
			("ft", 3),
			("up", 4),
			("dn", 5),
		};

		private List<Layer> LoadLayers(Shader shader)
		{
			var layers = new List<Layer>();

			foreach (var stage in shader.GetImageStages())
			{
				var bundle = stage.bundles[0];
				if (bundle.tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED || bundle.tcGen == TexCoordGen.TCGEN_LIGHTMAP)
					continue;

				var imagePath = resolveImagePath(Path.ChangeExtension(bundle.images[0], null));
				if (imagePath == null || !File.Exists(imagePath))
					continue; // skip a missing layer rather than abandoning the whole sky

				using var image = Image.Load<Rgba32>(imagePath);
				var pixels = new Rgba32[image.Width * image.Height];
				image.CopyPixelDataTo(pixels);

				layers.Add(new Layer
				{
					pixels = pixels,
					width = image.Width,
					height = image.Height,
					scale = GetScale(stage),
					flags = stage.flags,
					constColor = new Vector3(stage.constantColor[0], stage.constantColor[1], stage.constantColor[2]) / 255f,
					useConstColor = stage.rgbGen == ColorGen.CGEN_CONST,
				});
			}

			return layers;
		}

		// Q3's face index for straight down (see ioq3 tr_sky.c st_to_vec: axis 5 is "look straight down").
		// FillCloudBox always skips this face ("still don't want to draw the bottom, even if fullClouds"),
		// so the cloud dome is never projected underfoot - only the outerbox (or black, with no outerbox)
		// shows there. Reproduced here so the bake doesn't show a distorted dome projection where Q3 never
		// draws one.
		private const int DownFace = 5;

		// Bakes one face into an RGBA8888 buffer. Pixel (px,py) maps to the same dome view direction Q3's
		// outerbox sampling uses for this face (image u,v -> MakeSkyVec(2u-1, 1-2v, axis)), so the result is
		// what Q3 would have shown on that face. When boxFace is given, it's sampled at the same (px,py) -
		// pixel (px,py) is already exactly the outerbox's own UV for this face (see MakeSkyVec's outSt,
		// which derives from the same s,t) - and used as the base the cloud layers composite over, instead
		// of starting from black.
		private static byte[] BakeFace(int q3Face, List<Layer> layers, float cloudHeight, Layer? boxFace)
		{
			var buffer = new byte[FaceSize * FaceSize * 4];
			var skipClouds = q3Face == DownFace;

			for (var py = 0; py < FaceSize; py++)
			{
				var t = 1f - 2f * ((py + 0.5f) / FaceSize);
				for (var px = 0; px < FaceSize; px++)
				{
					var s = 2f * ((px + 0.5f) / FaceSize) - 1f;

					var baseColor = boxFace != null ?
						SampleBilinearClamp(boxFace, (px + 0.5f) / FaceSize, (py + 0.5f) / FaceSize) :
						Vector3.Zero;

					var color = baseColor;
					if (!skipClouds)
					{
						var dir = MakeSkyVec(s, t, q3Face);
						color = CloudColor(dir, layers, cloudHeight, baseColor);
					}

					var index = (py * FaceSize + px) * 4;
					buffer[index + 0] = (byte)(Math.Clamp(color.X, 0f, 1f) * 255f + 0.5f);
					buffer[index + 1] = (byte)(Math.Clamp(color.Y, 0f, 1f) * 255f + 0.5f);
					buffer[index + 2] = (byte)(Math.Clamp(color.Z, 0f, 1f) * 255f + 0.5f);
					buffer[index + 3] = 255;
				}
			}

			return buffer;
		}

		// ioq3 tr_sky.c: MakeSkyVec - turns face grid coords (s,t in -1..1) into a world-space view direction
		// for the given box face. boxSize is dropped (the cloud projection below is invariant to its scale).
		private static readonly int[][] StToVec =
		{
			new[] {  3, -1,  2 },
			new[] { -3,  1,  2 },
			new[] {  1,  3,  2 },
			new[] { -1, -3,  2 },
			new[] { -2, -1,  3 },
			new[] {  2, -1, -3 },
		};

		private static Vector3 MakeSkyVec(float s, float t, int axis)
		{
			Span<float> b = stackalloc float[3] { s, t, 1f };
			Span<float> outv = stackalloc float[3];

			for (var j = 0; j < 3; j++)
			{
				var k = StToVec[axis][j];
				outv[j] = k < 0 ? -b[-k - 1] : b[k - 1];
			}

			return new Vector3(outv[0], outv[1], outv[2]);
		}

		// ioq3 tr_sky.c: R_InitSkyTexCoords - intersect the view ray with the cloud dome (sphere of radius
		// RadiusWorld whose top sits cloudHeight above the viewer), then derive the cloud texcoord from the
		// normalized intersection point. Composites every cloud stage at that texcoord, starting from
		// baseColor (the outerbox's own pixel here, or black for a dome-only sky with no outerbox). The
		// intersection point is invariant to the length of dir, so an unnormalized MakeSkyVec direction is fine.
		private static Vector3 CloudColor(Vector3 dir, List<Layer> layers, float cloudHeight, Vector3 baseColor)
		{
			var len2 = Vector3.Dot(dir, dir);
			var disc = dir.Z * dir.Z * RadiusWorld * RadiusWorld +
				len2 * (2f * RadiusWorld * cloudHeight + cloudHeight * cloudHeight);
			var p = (-dir.Z * RadiusWorld + MathF.Sqrt(disc)) / len2;

			var v = dir * p;
			v.Z += RadiusWorld;
			v = Vector3.Normalize(v);

			var baseSt = new Vector2(
				MathF.Acos(Math.Clamp(v.X, -1f, 1f)),
				MathF.Acos(Math.Clamp(v.Y, -1f, 1f)));

			var accum = baseColor;
			foreach (var layer in layers)
			{
				var sample = SampleBilinearWrap(layer, baseSt * layer.scale);
				var rgb = new Vector3(sample.X, sample.Y, sample.Z);
				if (layer.useConstColor)
					rgb *= layer.constColor;

				var srcFactor = SrcBlendFactor(layer.flags, rgb, sample.W, accum);
				var dstFactor = DstBlendFactor(layer.flags, rgb, sample.W);
				accum = rgb * srcFactor + accum * dstFactor;
			}

			return Vector3.Clamp(accum, Vector3.Zero, Vector3.One);
		}

		// GL blend factor for the incoming stage color. An unset src field (opaque base stage, e.g. hellsky's
		// depthWrite layer) means a straight ONE/ZERO replace.
		private static Vector3 SrcBlendFactor(ShaderStageFlags flags, Vector3 sampleRgb, float sampleA, Vector3 accum)
		{
			switch (flags & ShaderStageFlags.GLS_SRCBLEND_BITS)
			{
				case 0:
				case ShaderStageFlags.GLS_SRCBLEND_ONE: return Vector3.One;
				case ShaderStageFlags.GLS_SRCBLEND_ZERO: return Vector3.Zero;
				case ShaderStageFlags.GLS_SRCBLEND_DST_COLOR: return accum;
				case ShaderStageFlags.GLS_SRCBLEND_ONE_MINUS_DST_COLOR: return Vector3.One - accum;
				case ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA: return new Vector3(sampleA);
				case ShaderStageFlags.GLS_SRCBLEND_ONE_MINUS_SRC_ALPHA: return new Vector3(1f - sampleA);
				default: return Vector3.One;
			}
		}

		private static Vector3 DstBlendFactor(ShaderStageFlags flags, Vector3 sampleRgb, float sampleA)
		{
			switch (flags & ShaderStageFlags.GLS_DSTBLEND_BITS)
			{
				case 0:
				case ShaderStageFlags.GLS_DSTBLEND_ZERO: return Vector3.Zero;
				case ShaderStageFlags.GLS_DSTBLEND_ONE: return Vector3.One;
				case ShaderStageFlags.GLS_DSTBLEND_SRC_COLOR: return sampleRgb;
				case ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_COLOR: return Vector3.One - sampleRgb;
				case ShaderStageFlags.GLS_DSTBLEND_SRC_ALPHA: return new Vector3(sampleA);
				case ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA: return new Vector3(1f - sampleA);
				default: return Vector3.Zero;
			}
		}

		// Combined tcMod scale magnitude for a stage (Q3 multiplies all tcMod scales together).
		private static Vector2 GetScale(ShaderStage stage)
		{
			var scale = Vector2.One;
			foreach (var texMod in stage.bundles[0].texMods)
			{
				if (texMod.type == TexMod.TMOD_SCALE)
					scale *= new Vector2(texMod.scale[0], texMod.scale[1]);
			}
			return scale;
		}

		private static float ParseCloudHeight(string cloudHeight)
		{
			// Matches Q3: atof, and a missing/zero value falls back to 512.
			if (float.TryParse(cloudHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h != 0f)
				return h;

			return DefaultCloudHeight;
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

		// Samples an outerbox face at normalized (u,v) in 0..1, clamped at the edges (unlike the cloud layers'
		// wrapping sample) since a box face is a single non-tiling image, not a repeating pattern.
		private static Vector3 SampleBilinearClamp(Layer layer, float u, float v)
		{
			var w = layer.width;
			var h = layer.height;

			var fx = Math.Clamp(u * w - 0.5f, 0f, w - 1f);
			var fy = Math.Clamp(v * h - 0.5f, 0f, h - 1f);

			var x0 = (int)MathF.Floor(fx);
			var y0 = (int)MathF.Floor(fy);
			var x1 = Math.Min(x0 + 1, w - 1);
			var y1 = Math.Min(y0 + 1, h - 1);
			var dx = fx - x0;
			var dy = fy - y0;

			var c00 = ToVector(layer.pixels[y0 * w + x0]);
			var c10 = ToVector(layer.pixels[y0 * w + x1]);
			var c01 = ToVector(layer.pixels[y1 * w + x0]);
			var c11 = ToVector(layer.pixels[y1 * w + x1]);

			var lerped = Vector4.Lerp(Vector4.Lerp(c00, c10, dx), Vector4.Lerp(c01, c11, dx), dy);
			return new Vector3(lerped.X, lerped.Y, lerped.Z);
		}

		private static Vector4 ToVector(Rgba32 pixel) => new(pixel.R / 255f, pixel.G / 255f, pixel.B / 255f, pixel.A / 255f);

		// Writes a single opaque skybox face VTF. Clamped (CLAMP_S/T) so the dome edges of adjacent faces
		// don't bleed/wrap into a visible seam.
		private bool BakeFaceVtf(string baseTexture, byte[] rgba)
		{
			var vtfPath = Path.Combine(pk3Dir, baseTexture + ".vtf");
			Directory.CreateDirectory(Path.GetDirectoryName(vtfPath)!);

			using var vtf = new VTF();
			vtf.Version = 6;

			if (!vtf.SetImage(rgba, ImageFormat.RGBA8888, FaceSize, FaceSize))
				return false;

			vtf.ComputeReflectivity();
			vtf.SetRecommendedMipCount();
			vtf.ComputeMips();
			vtf.SetFormat(ImageFormat.STRATA_BC7);
			vtf.AddFlags(VTF.Flags.V0_CLAMP_S | VTF.Flags.V0_CLAMP_T);
			vtf.ComputeTransparencyFlags();

			return vtf.Bake(vtfPath);
		}

		private void WriteSkyboxVmt(string baseTexture)
		{
			var vmt = $$"""
				UnlitGeneric
				{
					"$basetexture" "{{baseTexture}}"
					"$nofog" 1
					"$ignorez" 1
				}
				""";

			var vmtPath = Path.Combine(pk3Dir, baseTexture + ".vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath)!);
			File.WriteAllText(vmtPath, vmt);
		}
	}
}
