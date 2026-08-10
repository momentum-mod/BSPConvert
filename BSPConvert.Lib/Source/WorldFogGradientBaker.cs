using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using LibBSP;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BSPConvert.Lib
{
	// Bakes Quake 3 "world-space fog gradient" overlays into the lightmap. The idiom (e.g. dfwc2021's
	// hgb-supersecret/*_fog shaders) alpha-blends a small gradient texture over a lightmapped base, projected
	// through "tcGen vector" so its texcoords are read from world position - typically a vertical height fade
	// (S is collapsed, T = worldZ * scale + offset), clamped so it holds a flat value above/below a band.
	//
	// Source has no world-space texcoord projection, so no material can reproduce this. Instead we evaluate the
	// gradient per luxel using each luxel's world position and blend it into the Q3 lightmap before conversion,
	// exactly where Q3's own alpha-blend ran (gamma/framebuffer space). The base texture stays the material's
	// $basetexture, so only the lighting carries the fog. Baking into the lightmap means the overlay's small
	// additive color term is also multiplied by the base texel - a negligible error for the dark fog colors
	// this idiom uses, and the dominant darkening term is exact.
	public class WorldFogGradientBaker
	{
		// A prepared fog overlay: the gradient image plus how it projects from world space.
		public class FogOverlay
		{
			public Rgba32[] pixels = Array.Empty<Rgba32>();
			public int width;
			public int height;
			public bool clamp;              // clampMap -> clamp-to-edge addressing (else wrap)
			public Vector3 vecS;            // tcGen vector: S = dot(worldPos, vecS)
			public Vector3 vecT;            // tcGen vector: T = dot(worldPos, vecT)
			public List<TexModInfo> texMods = new();
			public Vector3 constColor = Vector3.One; // rgbGen const tint (else texture color used as-is)
			public bool useConstColor;
			public float constAlpha = 1f;   // alphaGen const (else texture alpha used as-is)
			public bool useConstAlpha;
		}

		// Affine map from a face's lightmap UV (uv1, page-relative 0..1) to world position: worldPos = cU*u +
		// cV*v + c1. Fit by least squares over the face vertices; exact for a planar face, a close approximation
		// on the rare curved patch. Degenerate (zero-area lightmap) faces collapse to a constant centroid.
		public readonly struct LuxelToWorld
		{
			private readonly Vector3 cU;
			private readonly Vector3 cV;
			private readonly Vector3 c1;

			public LuxelToWorld(Vector3 cU, Vector3 cV, Vector3 c1)
			{
				this.cU = cU;
				this.cV = cV;
				this.c1 = c1;
			}

			public Vector3 At(float u, float v) => cU * u + cV * v + c1;
		}

		private readonly Dictionary<string, string> pk3ImageDict;
		private readonly Dictionary<string, string> q3ImageDict;
		private readonly Dictionary<string, string> customImageDict;
		private readonly Dictionary<string, FogOverlay?> cache = new();

		public WorldFogGradientBaker(string pk3Dir)
		{
			pk3ImageDict = BuildLookup(pk3Dir);
			q3ImageDict = BuildLookup(ContentManager.GetQ3ContentDir());
			customImageDict = BuildLookup(ContentManager.GetCustomContentDir());
		}

		// The overlay stage of a world-space fog gradient shader: an alpha-blended "tcGen vector" stage sitting
		// over a lightmapped base. Returns null for any other shader - this is the detection the bake keys off.
		public static ShaderStage? GetFogGradientStage(Shader shader)
		{
			if (shader?.stages == null)
				return null;
			if (!ShaderStageUtils.HasLightmapStage(shader))
				return null;

			return shader.stages.FirstOrDefault(s =>
				s.bundles[0].tcGen == TexCoordGen.TCGEN_VECTOR &&
				!string.IsNullOrEmpty(s.bundles[0].images[0]) &&
				ShaderStageUtils.IsAlphaBlend(s));
		}

		// Loads (and caches) the fog overlay for a shader, or null if it isn't a world-space fog gradient shader
		// or its gradient image can't be found.
		public FogOverlay? GetOverlay(string textureName, Shader shader)
		{
			if (cache.TryGetValue(textureName, out var cached))
				return cached;

			var overlay = LoadOverlay(shader);
			cache[textureName] = overlay;
			return overlay;
		}

		private FogOverlay? LoadOverlay(Shader shader)
		{
			var stage = GetFogGradientStage(shader);
			if (stage == null)
				return null;

			var bundle = stage.bundles[0];
			var imagePath = ResolveImagePath(Path.ChangeExtension(bundle.images[0], null));
			if (imagePath == null || !File.Exists(imagePath))
				return null;

			using var image = Image.Load<Rgba32>(imagePath);
			var pixels = new Rgba32[image.Width * image.Height];
			image.CopyPixelDataTo(pixels);

			return new FogOverlay
			{
				pixels = pixels,
				width = image.Width,
				height = image.Height,
				clamp = bundle.clamp,
				vecS = bundle.tcGenVectors[0],
				vecT = bundle.tcGenVectors[1],
				texMods = bundle.texMods,
				constColor = new Vector3(stage.constantColor[0], stage.constantColor[1], stage.constantColor[2]) / 255f,
				useConstColor = stage.rgbGen == ColorGen.CGEN_CONST,
				constAlpha = stage.constantColor[3] / 255f,
				useConstAlpha = stage.alphaGen == AlphaGen.AGEN_CONST,
			};
		}

		// Blends the fog overlay into one Q3 lightmap luxel (raw gamma bytes), sampled at the luxel's world
		// position (u, v are its page-relative lightmap coords). Returns a new pre-overbright luxel byte.
		//
		// Quake 3 renders the overlay's "GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA" pass in the framebuffer AFTER the
		// lightmap's 4x display overbright and its clamp to white, so the fog darkens the already-bright surface
		// immediately and linearly. This matters because ColorUtil re-applies that overbright when converting the
		// luxel: blending the raw pre-overbright value instead leaves the darkening hidden in the overbright
		// headroom (too light, too soft a taper) and scales the fog color itself 4x too bright. So reproduce the
		// blend in Q3's display space (overbright + clamp), then divide the overbright back out so the downstream
		// conversion re-applies it to land on Q3's actual displayed color.
		//
		// 'overbright' must match the factor the caller's conversion applies (ColorUtil.OVERBRIGHT for internal
		// lightmaps, 1 for external ones, which are converted without it) so the two cancel exactly.
		public (byte r, byte g, byte b) Blend(FogOverlay overlay, in LuxelToWorld map, byte r, byte g, byte b, float u, float v, int overbright = ColorUtil.OVERBRIGHT)
		{
			var world = map.At(u, v);
			var (fr, fg, fb, fa) = Sample(overlay, world);
			if (fa <= 0f)
				return (r, g, b);

			return (
				FogChannel(r, fr, fa, overbright),
				FogChannel(g, fg, fa, overbright),
				FogChannel(b, fb, fa, overbright));
		}

		// Composites one fog channel in Q3's post-overbright display space, then maps back to a pre-overbright
		// luxel byte. lit = overbright, clamped to display white; disp = the fog alpha-blend over it (the fog
		// color is a plain framebuffer texel, not overbright-scaled); dividing by overbright undoes the overbright
		// the lightmap conversion adds back. See Blend.
		private static byte FogChannel(byte luxel, float fogColor, float fogAlpha, int overbright)
		{
			var lit = MathF.Min(luxel * overbright, 255f);
			var disp = lit * (1f - fogAlpha) + fogColor * fogAlpha;
			return (byte)Math.Clamp(disp / overbright, 0f, 255f);
		}

		// Samples the overlay at a world position: projects through the tcGen vectors + static tcMods, then
		// reads the gradient image. Returns fog color in 0-255 (matching the luxel byte space) and alpha 0-1.
		private (float r, float g, float b, float a) Sample(FogOverlay o, Vector3 world)
		{
			var s = Vector3.Dot(world, o.vecS);
			var t = Vector3.Dot(world, o.vecT);
			ApplyTexMods(o.texMods, ref s, ref t);

			var (r, g, b, a) = SampleBilinear(o, s, t);

			if (o.useConstColor)
			{
				r *= o.constColor.X;
				g *= o.constColor.Y;
				b *= o.constColor.Z;
			}
			if (o.useConstAlpha)
				a *= o.constAlpha;

			return (r, g, b, a);
		}

		// Applies the static texcoord transforms of the fog stage to a projected (s, t). Time-varying mods
		// (scroll/rotate/stretch/turb) are evaluated at rest (t=0), i.e. skipped, so the bake is the static look.
		private static void ApplyTexMods(List<TexModInfo> texMods, ref float s, ref float t)
		{
			foreach (var tm in texMods)
			{
				switch (tm.type)
				{
					case TexMod.TMOD_TRANSFORM:
						// Q3 RB_CalcTransformTexCoords: st' = (s*m00 + t*m10 + tr0, s*m01 + t*m11 + tr1).
						var ns = s * tm.matrix[0][0] + t * tm.matrix[1][0] + tm.translate[0];
						var nt = s * tm.matrix[0][1] + t * tm.matrix[1][1] + tm.translate[1];
						s = ns;
						t = nt;
						break;
					case TexMod.TMOD_SCALE:
						s *= tm.scale[0];
						t *= tm.scale[1];
						break;
				}
			}
		}

		private static (float r, float g, float b, float a) SampleBilinear(FogOverlay o, float s, float t)
		{
			// Texel-center convention: texel i covers [i, i+1), center at i+0.5. t=0 is the top row (row 0),
			// matching Q3's top-down texture upload.
			var fx = s * o.width - 0.5f;
			var fy = t * o.height - 0.5f;
			var x0 = (int)MathF.Floor(fx);
			var y0 = (int)MathF.Floor(fy);
			var tx = fx - x0;
			var ty = fy - y0;

			var top = Vector4.Lerp(Texel(o, x0, y0), Texel(o, x0 + 1, y0), tx);
			var bot = Vector4.Lerp(Texel(o, x0, y0 + 1), Texel(o, x0 + 1, y0 + 1), tx);
			var c = Vector4.Lerp(top, bot, ty);
			return (c.X, c.Y, c.Z, c.W);
		}

		// One texel as (R, G, B in 0-255, A in 0-1), with the overlay's clamp/wrap addressing.
		private static Vector4 Texel(FogOverlay o, int x, int y)
		{
			x = Address(x, o.width, o.clamp);
			y = Address(y, o.height, o.clamp);
			var p = o.pixels[y * o.width + x];
			return new Vector4(p.R, p.G, p.B, p.A / 255f);
		}

		private static int Address(int i, int size, bool clamp)
		{
			if (clamp)
				return Math.Clamp(i, 0, size - 1);

			i %= size;
			if (i < 0)
				i += size;
			return i;
		}

		// Fits the affine lightmap-UV -> world map for a face by least squares over its vertices. Solves the
		// normal equations for world_k = cU_k*u + cV_k*v + c1_k with basis [u, v, 1]: M = sum(b b^T),
		// rhs = sum(b * world). Falls back to the vertex centroid (uniform fog) when the lightmap is degenerate.
		public static LuxelToWorld BuildLuxelToWorld(IEnumerable<Vertex> vertices)
		{
			double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
			Vector3 r0 = Vector3.Zero, r1 = Vector3.Zero, r2 = Vector3.Zero;
			var centroid = Vector3.Zero;
			var n = 0;

			foreach (var vert in vertices)
			{
				double u = vert.uv1.X;
				double v = vert.uv1.Y;
				var p = vert.position;

				m00 += u * u;
				m01 += u * v;
				m02 += u;
				m11 += v * v;
				m12 += v;
				m22 += 1;

				r0 += p * (float)u;
				r1 += p * (float)v;
				r2 += p;
				centroid += p;
				n++;
			}

			if (n == 0)
				return new LuxelToWorld(Vector3.Zero, Vector3.Zero, Vector3.Zero);

			centroid /= n;

			if (!TryInvertSymmetric3x3(m00, m01, m02, m11, m12, m22, out var inv))
				return new LuxelToWorld(Vector3.Zero, Vector3.Zero, centroid); // degenerate -> constant fog

			var cU = (float)inv.i00 * r0 + (float)inv.i01 * r1 + (float)inv.i02 * r2;
			var cV = (float)inv.i01 * r0 + (float)inv.i11 * r1 + (float)inv.i12 * r2;
			var c1 = (float)inv.i02 * r0 + (float)inv.i12 * r1 + (float)inv.i22 * r2;
			return new LuxelToWorld(cU, cV, c1);
		}

		// Inverse of the symmetric 3x3 [[m00,m01,m02],[m01,m11,m12],[m02,m12,m22]] via cofactors. Returns false
		// when it's (near-)singular, i.e. the face's lightmap UVs are collinear/degenerate and carry no plane.
		private static bool TryInvertSymmetric3x3(double m00, double m01, double m02, double m11, double m12, double m22,
			out (double i00, double i01, double i02, double i11, double i12, double i22) inv)
		{
			var c00 = m11 * m22 - m12 * m12;
			var c01 = m02 * m12 - m01 * m22;
			var c02 = m01 * m12 - m02 * m11;
			var det = m00 * c00 + m01 * c01 + m02 * c02;

			var maxDiag = Math.Max(m00, Math.Max(m11, m22));
			var eps = 1e-9 * maxDiag * maxDiag * maxDiag;
			if (Math.Abs(det) <= eps)
			{
				inv = default;
				return false;
			}

			var invDet = 1.0 / det;
			inv = (
				c00 * invDet,
				c01 * invDet,
				c02 * invDet,
				(m00 * m22 - m02 * m02) * invDet,
				(m02 * m01 - m00 * m12) * invDet,
				(m00 * m11 - m01 * m01) * invDet);
			return true;
		}

		private string? ResolveImagePath(string texturePath)
		{
			var key = texturePath.ToLower(CultureInfo.InvariantCulture);
			if (pk3ImageDict.TryGetValue(key, out var path) ||
				q3ImageDict.TryGetValue(key, out path) ||
				customImageDict.TryGetValue(key, out path))
				return path;

			return null;
		}

		// Maps relative texture paths (no extension) to full file paths in a content folder, mirroring
		// MaterialConverter's lookup so the bake resolves the same images the material path does.
		private static Dictionary<string, string> BuildLookup(string contentDir)
		{
			var imageDict = new Dictionary<string, string>();

			if (!Directory.Exists(contentDir))
				return imageDict;

			foreach (var file in Directory.GetFiles(contentDir, "*.*", SearchOption.AllDirectories))
			{
				var ext = Path.GetExtension(file);
				if (ext != ".tga" && ext != ".jpg")
					continue;

				var texturePath = file
					.Replace(contentDir + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
					.Replace(Path.DirectorySeparatorChar, '/')
					.Replace(ext, "", StringComparison.OrdinalIgnoreCase)
					.ToLower(CultureInfo.InvariantCulture);

				if (!imageDict.ContainsKey(texturePath))
					imageDict.Add(texturePath, file);
			}

			return imageDict;
		}
	}
}
