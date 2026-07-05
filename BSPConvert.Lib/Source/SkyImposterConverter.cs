using System;
using System.IO;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using sourcepp.vtfpp;

namespace BSPConvert.Lib
{
	// Converts a Q3 sky shader into a Source "WindowImposter" material: a 6-sided cubemap VTF referenced by
	// $envmap, drawn directly on the sky brush face as a fake skybox. Unlike the global-skybox path (one
	// worldspawn skyname + SURF_SKY per map), each WindowImposter material is independent, so a map with
	// several distinct sky shaders can show a different sky on each brush (see BSPConverter.UseSkyImposters).
	// Handles both real image-box skies (skyParms <outerbox>) and dynamic cloud skies; the cubemap is baked
	// by sampling the sky in the world direction of each face texel, reusing CloudSkyboxBaker's dome
	// projection for cloud skies and the outerbox images (via ioq3's MakeSkyVec mapping) for image skies.
	public class SkyImposterConverter
	{
		// Cubemap face resolution. Matches CloudSkyboxBaker's baked-face size - enough for the dome/outerbox
		// projection to resolve without visible blockiness while staying cheap (6 faces).
		private const int FaceSize = 512;

		// Q3 outerbox image suffix per ioq3 sky axis (st_to_vec index). Axis order matches MakeSkyVec/
		// st_to_vec; the suffixes are the Q3 <name>_<suffix>.tga images the 6-sided skybox path also uses.
		private static readonly string[] AxisSuffix = { "rt", "lf", "bk", "ft", "up", "dn" };

		private static readonly Vector3 X = Vector3.UnitX;
		private static readonly Vector3 Y = Vector3.UnitY;
		private static readonly Vector3 Z = Vector3.UnitZ;

		// The 6 cubemap faces in Source/VTF storage order (+X, -X, +Y, -Y, +Z, -Z), each given as the
		// world-space basis of its stored image: (forward through the face center, +u across a row, +v down
		// a column). This is the standard D3D cube-map convention that Source's texCUBE sampling uses, with
		// cube space taken as world space (X forward, Y left, Z up). Individual faces look "sideways" as flat
		// images - that is expected, since texCUBE reads them back in the same convention.
		// ** This table is the single knob to adjust if the sky appears rotated/mirrored/on the wrong face
		// in-engine. **
		private static readonly (Vector3 forward, Vector3 right, Vector3 down)[] CubeFaces =
		{
			( X, -Z, -Y), // +X
			(-X,  Z, -Y), // -X
			( Y,  X,  Z), // +Y
			(-Y,  X, -Z), // -Y
			( Z,  X, -Y), // +Z
			(-Z, -X, -Y), // -Z
		};

		private readonly string pk3Dir;
		private readonly Func<string, string?> resolveImagePath;
		private readonly CloudSkyboxBaker cloudSkyboxBaker;

		public SkyImposterConverter(string pk3Dir, Func<string, string?> resolveImagePath, CloudSkyboxBaker cloudSkyboxBaker)
		{
			this.pk3Dir = pk3Dir;
			this.resolveImagePath = resolveImagePath;
			this.cloudSkyboxBaker = cloudSkyboxBaker;
		}

		// A sky shader the imposter path can convert: a real image-box sky or a bakeable cloud sky.
		public static bool IsSkyShader(Shader shader)
		{
			return (shader?.skyParms != null && shader.skyParms.HasImageBox) ||
				CloudSkyboxBaker.IsCloudSkyShader(shader);
		}

		// Bakes the cubemap VTF + writes the WindowImposter VMT for a sky shader. Returns false (leaving the
		// shader to the normal material path) if no sky imagery can be loaded or the VTF fails to bake.
		public bool TryConvert(string textureName, Shader shader)
		{
			var sampler = CreateSampler(shader);
			if (sampler == null)
				return false;

			var cubemapTexture = $"{textureName}_cube";
			if (!BakeCubemapVtf(cubemapTexture, sampler))
				return false;

			WriteImposterVmt(textureName, cubemapTexture);
			return true;
		}

		// Builds a world-direction -> RGB sampler for the sky. Cloud skies reuse the dome projection;
		// image-box skies sample the 6 outerbox images. Returns null if no sky imagery can be loaded.
		private Func<Vector3, Vector3>? CreateSampler(Shader shader)
		{
			if (shader.skyParms != null && shader.skyParms.HasImageBox)
				return CreateImageBoxSampler(shader.skyParms.outerBox);

			return cloudSkyboxBaker.TryCreateCloudSampler(shader);
		}

		private Func<Vector3, Vector3>? CreateImageBoxSampler(string outerBox)
		{
			var faces = new SkyImage?[6];
			var loadedAny = false;
			for (var axis = 0; axis < 6; axis++)
			{
				var path = resolveImagePath($"{outerBox}_{AxisSuffix[axis]}");
				if (path == null || !File.Exists(path))
					continue; // skip a missing face rather than abandoning the whole sky

				using var image = Image.Load<Rgba32>(path);
				var pixels = new Rgba32[image.Width * image.Height];
				image.CopyPixelDataTo(pixels);

				faces[axis] = new SkyImage { pixels = pixels, width = image.Width, height = image.Height };
				loadedAny = true;
			}

			if (!loadedAny)
				return null;

			return dir => SampleImageBox(faces, dir);
		}

		// Inverse of ioq3 MakeSkyVec (tr_sky.c): maps a world direction to the Q3 outerbox face + texcoord it
		// came from (st_to_vec), so the cubemap is sampled from the same imagery the 6-sided skybox path uses.
		// ioq3's texcoord mapping is u = (s + 1) / 2, v = (1 - t) / 2.
		private static Vector3 SampleImageBox(SkyImage?[] faces, Vector3 dir)
		{
			var ax = MathF.Abs(dir.X);
			var ay = MathF.Abs(dir.Y);
			var az = MathF.Abs(dir.Z);

			int axis;
			float s, t;
			if (ax >= ay && ax >= az)
			{
				if (dir.X > 0) { axis = 0; var m = dir.X; s = -dir.Y / m; t = dir.Z / m; }   // +X (rt)
				else { axis = 1; var m = -dir.X; s = dir.Y / m; t = dir.Z / m; }              // -X (lf)
			}
			else if (ay >= ax && ay >= az)
			{
				if (dir.Y > 0) { axis = 2; var m = dir.Y; s = dir.X / m; t = dir.Z / m; }     // +Y (bk)
				else { axis = 3; var m = -dir.Y; s = -dir.X / m; t = dir.Z / m; }             // -Y (ft)
			}
			else
			{
				if (dir.Z > 0) { axis = 4; var m = dir.Z; s = -dir.Y / m; t = -dir.X / m; }   // +Z (up)
				else { axis = 5; var m = -dir.Z; s = -dir.Y / m; t = dir.X / m; }             // -Z (dn)
			}

			var face = faces[axis];
			if (face == null)
				return Vector3.Zero; // missing face image -> black

			var u = (s + 1f) * 0.5f;
			var v = (1f - t) * 0.5f;
			return SampleClamped(face, u, v);
		}

		private static Vector3 SampleClamped(SkyImage img, float u, float v)
		{
			var fx = Math.Clamp(u, 0f, 1f) * img.width - 0.5f;
			var fy = Math.Clamp(v, 0f, 1f) * img.height - 0.5f;

			var x0 = (int)MathF.Floor(fx);
			var y0 = (int)MathF.Floor(fy);
			var dx = fx - x0;
			var dy = fy - y0;

			var x0c = Math.Clamp(x0, 0, img.width - 1);
			var y0c = Math.Clamp(y0, 0, img.height - 1);
			var x1c = Math.Clamp(x0 + 1, 0, img.width - 1);
			var y1c = Math.Clamp(y0 + 1, 0, img.height - 1);

			var c00 = ToVector(img.pixels[y0c * img.width + x0c]);
			var c10 = ToVector(img.pixels[y0c * img.width + x1c]);
			var c01 = ToVector(img.pixels[y1c * img.width + x0c]);
			var c11 = ToVector(img.pixels[y1c * img.width + x1c]);

			return Vector3.Lerp(Vector3.Lerp(c00, c10, dx), Vector3.Lerp(c01, c11, dx), dy);
		}

		private static Vector3 ToVector(Rgba32 p) => new(p.R / 255f, p.G / 255f, p.B / 255f);

		private bool BakeCubemapVtf(string cubemapTexture, Func<Vector3, Vector3> sampler)
		{
			var faces = new byte[6][];
			for (var i = 0; i < 6; i++)
				faces[i] = BakeCubeFace(CubeFaces[i], sampler);

			var vtfPath = Path.Combine(pk3Dir, cubemapTexture + ".vtf");
			Directory.CreateDirectory(Path.GetDirectoryName(vtfPath)!);

			using var vtf = new VTF();
			vtf.Version = 6;

			// Establish dimensions/format from the first face, expand to a 6-face cubemap, then fill every
			// face (the expand may clear the initial face, so all 6 are written afterward).
			if (!vtf.SetImage(faces[0], ImageFormat.RGBA8888, FaceSize, FaceSize))
				return false;
			if (!vtf.SetFaceCount(true))
				return false;
			for (byte i = 0; i < 6; i++)
			{
				if (!vtf.SetImage(faces[i], ImageFormat.RGBA8888, FaceSize, FaceSize, face: i))
					return false;
			}

			vtf.ComputeReflectivity();
			vtf.SetRecommendedMipCount();
			vtf.ComputeMips();
			vtf.SetFormat(ImageFormat.STRATA_BC7);
			vtf.AddFlags(VTF.Flags.V0_ENVMAP); // mark as an environment cubemap so the engine samples all 6 faces
			vtf.ComputeTransparencyFlags();

			return vtf.Bake(vtfPath);
		}

		// Bakes one cubemap face into an RGBA8888 buffer. Each texel's world direction is forward + u*right +
		// v*down (u,v in -1..1 across the face); the sky color in that direction is sampled and stored.
		private static byte[] BakeCubeFace((Vector3 forward, Vector3 right, Vector3 down) basis, Func<Vector3, Vector3> sampler)
		{
			var buffer = new byte[FaceSize * FaceSize * 4];

			for (var py = 0; py < FaceSize; py++)
			{
				var v = 2f * ((py + 0.5f) / FaceSize) - 1f;
				for (var px = 0; px < FaceSize; px++)
				{
					var u = 2f * ((px + 0.5f) / FaceSize) - 1f;

					var dir = Vector3.Normalize(basis.forward + u * basis.right + v * basis.down);
					var color = Vector3.Clamp(sampler(dir), Vector3.Zero, Vector3.One);

					var index = (py * FaceSize + px) * 4;
					buffer[index + 0] = (byte)(color.X * 255f + 0.5f);
					buffer[index + 1] = (byte)(color.Y * 255f + 0.5f);
					buffer[index + 2] = (byte)(color.Z * 255f + 0.5f);
					buffer[index + 3] = 255;
				}
			}

			return buffer;
		}

		private void WriteImposterVmt(string textureName, string cubemapTexture)
		{
			var vmt = $$"""
				"WindowImposter"
				{
					"$envmap" "{{cubemapTexture}}"
					"$nofog" "1"
				}
				""";

			var vmtPath = Path.Combine(pk3Dir, textureName + ".vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath)!);
			File.WriteAllText(vmtPath, vmt);
		}

		// One outerbox face image loaded into memory for directional sampling.
		private class SkyImage
		{
			public Rgba32[] pixels = Array.Empty<Rgba32>();
			public int width;
			public int height;
		}
	}
}
