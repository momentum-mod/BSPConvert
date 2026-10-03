using sourcepp.vtfpp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// How a brush entity's rendermode blends its surfaces
	public enum BlendMode
	{
		Opaque,
		// kRenderTransTexture (and kRenderTransColor, approximated): blended by renderamt
		Translucent,
		// kRenderTransAdd: added, scaled by renderamt
		Additive
	}

	// Converts GoldSrc miptextures to a VTF and VMT each, written under the content directory as
	// <contentDir>/<materialName>.vtf/.vmt (the layout SourceBspBuilder reads texdata from).
	public class GoldSrcMaterialConverter
	{
		// Palette index drawn transparent on '{' textures
		private const byte TransparentIndex = 255;
		// GoldSrc advances animated textures at a fixed 10 frames per second
		private const int AnimationFrameRate = 10;

		private static readonly VTF.CreationOptions VtfOptions = new VTF.CreationOptions
		{
			Version = 6,
			CompressionLevel = 0,
			OutputFormat = ImageFormat.STRATA_BC7,
			// GoldSrc textures are multiples of 16 but often not powers of two. Keep their size so they stay sharp;
			// the VMT's mapping size keeps texture coordinates right whatever the VTF's size.
			WidthResizeMethod = ImageConversion.ResizeMethod.NONE,
			HeightResizeMethod = ImageConversion.ResizeMethod.NONE,
			ComputeMips = 1,
			ComputeThumbnail = 1,
			ComputeReflectivity = 1,
			ComputeTransparencyFlags = 1,
		};

		// Skybox faces are clamped so filtering doesn't wrap around to the opposite edge and leave seams, and drawn
		// at about their full size, so like Valve's skyboxes they don't need mips
		private static readonly VTF.CreationOptions SkyboxVtfOptions = new VTF.CreationOptions
		{
			Version = 6,
			CompressionLevel = 0,
			OutputFormat = ImageFormat.STRATA_BC7,
			WidthResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_BIGGER,
			HeightResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_BIGGER,
			VTFFlags = VTF.Flags.V0_CLAMP_S | VTF.Flags.V0_CLAMP_T | VTF.Flags.V0_NO_MIP | VTF.Flags.V0_NO_LOD,
			ComputeMips = 0,
			ComputeThumbnail = 1,
			ComputeReflectivity = 1,
		};

		private readonly string contentDir;
		private readonly List<string> writtenFiles = new List<string>();

		public GoldSrcMaterialConverter(string contentDir)
		{
			this.contentDir = contentDir;
		}

		// The VTFs and VMTs written so far
		public IReadOnlyList<string> WrittenFiles => writtenFiles;

		public bool Convert(string materialName, MipTexture texture)
		{
			var isAlphaTested = IsAlphaTested(texture.Name);
			var vtfPath = GetBasePath(materialName) + ".vtf";
			if (!VTF.Create(DecodeRGBA(texture, isAlphaTested), ImageFormat.RGBA8888, (ushort)texture.Width, (ushort)texture.Height, vtfPath, VtfOptions))
				return false;

			writtenFiles.Add(vtfPath);
			WriteVmt(materialName, materialName, texture, isAlphaTested, animated: false);
			return true;
		}

		// An animated texture sequence ("+0name", "+1name", ...): the frames go into one VTF stored under the first
		// frame's material, and every frame's material plays it at GoldSrc's 10 frames per second. GoldSrc picks the
		// frame from the clock, not from the frame a surface uses, so all of them show the same frame at a time.
		public bool ConvertAnimated(IReadOnlyList<string> frameMaterialNames, IReadOnlyList<MipTexture> frames)
		{
			var first = frames[0];
			var isAlphaTested = IsAlphaTested(first.Name);
			var vtfMaterialName = frameMaterialNames[0];
			var vtfPath = GetBasePath(vtfMaterialName) + ".vtf";

			using (var vtf = new VTF())
			{
				vtf.Version = VtfOptions.Version;
				vtf.ImageWidthResizeMethod = VtfOptions.WidthResizeMethod;
				vtf.ImageHeightResizeMethod = VtfOptions.HeightResizeMethod;
				var width = (ushort)first.Width;
				var height = (ushort)first.Height;
				if (!vtf.SetImage(DecodeRGBA(first, isAlphaTested), ImageFormat.RGBA8888, width, height) ||
					!vtf.SetFrameCount((ushort)frames.Count))
					return false;

				for (var i = 1; i < frames.Count; i++)
				{
					if (!vtf.SetImage(DecodeRGBA(frames[i], isAlphaTested), ImageFormat.RGBA8888, width, height, frame: (ushort)i))
						return false;
				}

				vtf.ComputeReflectivity();
				vtf.SetRecommendedMipCount();
				vtf.ComputeMips();
				vtf.SetFormat(VtfOptions.OutputFormat);
				vtf.ComputeTransparencyFlags();
				if (!vtf.Bake(vtfPath))
					return false;
			}

			writtenFiles.Add(vtfPath);
			foreach (var materialName in frameMaterialNames)
				WriteVmt(materialName, vtfMaterialName, first, isAlphaTested, animated: true);

			return true;
		}

		// A sprite as a Sprite material with a frame per sprite frame, which env_sprite plays at its framerate. Its
		// rendermode comes from the entity, as in GoldSrc.
		public bool ConvertSprite(string materialName, GoldSrcSprite sprite)
		{
			var basePath = GetBasePath(materialName);
			using (var vtf = new VTF())
			{
				vtf.Version = VtfOptions.Version;
				vtf.ImageWidthResizeMethod = VtfOptions.WidthResizeMethod;
				vtf.ImageHeightResizeMethod = VtfOptions.HeightResizeMethod;
				var width = (ushort)sprite.Width;
				var height = (ushort)sprite.Height;
				for (var i = 0; i < sprite.Frames.Count; i++)
				{
					var pixels = sprite.Frames[i];
					// Transparent texels take the color of their neighbors, so filtering doesn't darken the edges
					if (sprite.TextureFormat != SpriteTextureFormat.Additive)
						BleedIntoTransparentPixels(pixels, sprite.Width, sprite.Height);

					// The first frame sets the size the frames are allocated with
					if (!vtf.SetImage(pixels, ImageFormat.RGBA8888, width, height, frame: (ushort)i) ||
						(i == 0 && !vtf.SetFrameCount((ushort)sprite.Frames.Count)))
						return false;
				}

				// Sprites are drawn once, so their edges shouldn't wrap around
				vtf.AddFlags(VTF.Flags.V0_CLAMP_S | VTF.Flags.V0_CLAMP_T);
				vtf.ComputeReflectivity();
				vtf.SetRecommendedMipCount();
				vtf.ComputeMips();
				vtf.SetFormat(VtfOptions.OutputFormat);
				vtf.ComputeTransparencyFlags();
				if (!vtf.Bake(basePath + ".vtf"))
					return false;
			}

			writtenFiles.Add(basePath + ".vtf");

			var vmt = new StringBuilder();
			vmt.AppendLine("\"Sprite\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$spriteorientation\" \"{GetSpriteOrientation(sprite.Type)}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$spriteorigin\" \"[{sprite.OriginX:0.####} {sprite.OriginY:0.####}]\"");
			vmt.AppendLine("}");
			File.WriteAllText(basePath + ".vmt", vmt.ToString());
			writtenFiles.Add(basePath + ".vmt");

			return true;
		}

		private static string GetSpriteOrientation(SpriteType type)
		{
			return type switch
			{
				SpriteType.FacingUpright => "facing_upright",
				SpriteType.Parallel => "vp_parallel",
				SpriteType.Oriented => "oriented",
				SpriteType.ParallelOriented => "vp_parallel_oriented",
				_ => "parallel_upright",
			};
		}

		// A skybox face from a sky image (TGA or BMP), drawn fullbright like GoldSrc's sky
		public bool ConvertSkyboxFace(string materialName, string imagePath)
		{
			var basePath = GetBasePath(materialName);
			if (!VTF.Create(imagePath, basePath + ".vtf", SkyboxVtfOptions))
				return false;

			writtenFiles.Add(basePath + ".vtf");

			var vmt = new StringBuilder();
			vmt.AppendLine("\"UnlitGeneric\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			vmt.AppendLine("\t\"$nofog\" \"1\"");
			vmt.AppendLine("\t\"$ignorez\" \"1\"");
			vmt.AppendLine("}");
			File.WriteAllText(basePath + ".vmt", vmt.ToString());
			writtenFiles.Add(basePath + ".vmt");

			return true;
		}

		private string GetBasePath(string materialName)
		{
			var basePath = Path.Combine(contentDir, materialName.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
			return basePath;
		}

		// GoldSrc texture name without an animation or random tiling frame prefix ("+0", "+a", "-0")
		private static string GetBaseName(string textureName)
		{
			return textureName.Length > 2 && (textureName[0] == '+' || textureName[0] == '-') ? textureName.Substring(2) : textureName;
		}

		// Textures the engine draws as warped, unlit liquid (SURF_DRAWTURB)
		public static bool IsTurbulent(string textureName)
		{
			return textureName.StartsWith('!') ||
				textureName.StartsWith("water", StringComparison.OrdinalIgnoreCase) ||
				textureName.StartsWith("laser", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsAlphaTested(string textureName)
		{
			return GetBaseName(textureName).StartsWith('{');
		}

		// A material drawing an already converted texture (vtfMaterialName's VTF) the way a brush entity does: blended
		// by its rendermode (amount is its renderamt as 0-1) and scrolling at scrollSpeed, a func_conveyor's speed
		public void WriteVariant(string materialName, string vtfMaterialName, MipTexture texture, bool animated, BlendMode blendMode, float amount,
			float scrollSpeed)
		{
			WriteVmt(materialName, vtfMaterialName, texture, IsAlphaTested(texture.Name), animated, blendMode, amount, scrollSpeed);
		}

		private void WriteVmt(string materialName, string baseTexture, MipTexture texture, bool isAlphaTested, bool animated,
			BlendMode blendMode = BlendMode.Opaque, float amount = 1f, float scrollSpeed = 0f)
		{
			var vmtPath = GetBasePath(materialName) + ".vmt";
			File.WriteAllText(vmtPath, CreateVmt(baseTexture, texture, isAlphaTested, animated, blendMode, amount, scrollSpeed));
			writtenFiles.Add(vmtPath);
		}

		private static byte[] DecodeRGBA(MipTexture texture, bool isAlphaTested)
		{
			var palette = texture.Palette;
			var pixels = new byte[texture.Width * texture.Height * 4];
			for (var i = 0; i < texture.Pixels.Length; i++)
			{
				var index = texture.Pixels[i];
				pixels[i * 4] = palette[index * 3];
				pixels[i * 4 + 1] = palette[index * 3 + 1];
				pixels[i * 4 + 2] = palette[index * 3 + 2];
				pixels[i * 4 + 3] = isAlphaTested && index == TransparentIndex ? (byte)0 : (byte)255;
			}

			if (isAlphaTested)
				BleedIntoTransparentPixels(pixels, texture.Width, texture.Height);

			return pixels;
		}

		// Transparent texels keep the palette's key color (usually pure blue), which bilinear filtering and mipmaps
		// blend into the visible edges. Give them the average color of their opaque neighbors instead, growing out
		// from the edges until every transparent texel has one.
		private static void BleedIntoTransparentPixels(byte[] pixels, int width, int height)
		{
			var filled = new bool[width * height];
			var anyOpaque = false;
			for (var i = 0; i < filled.Length; i++)
			{
				filled[i] = pixels[i * 4 + 3] != 0;
				anyOpaque |= filled[i];
			}

			if (!anyOpaque)
				return;

			var newlyFilled = new List<(int index, byte r, byte g, byte b)>();
			do
			{
				newlyFilled.Clear();
				for (var y = 0; y < height; y++)
				{
					for (var x = 0; x < width; x++)
					{
						var i = y * width + x;
						if (filled[i])
							continue;

						int r = 0, g = 0, b = 0, count = 0;
						for (var dy = -1; dy <= 1; dy++)
						{
							for (var dx = -1; dx <= 1; dx++)
							{
								// Textures tile, so neighbors wrap around
								var n = ((y + dy + height) % height) * width + (x + dx + width) % width;
								if (!filled[n])
									continue;

								r += pixels[n * 4];
								g += pixels[n * 4 + 1];
								b += pixels[n * 4 + 2];
								count++;
							}
						}

						if (count > 0)
							newlyFilled.Add((i, (byte)(r / count), (byte)(g / count), (byte)(b / count)));
					}
				}

				foreach (var (index, r, g, b) in newlyFilled)
				{
					pixels[index * 4] = r;
					pixels[index * 4 + 1] = g;
					pixels[index * 4 + 2] = b;
					filled[index] = true;
				}
			}
			while (newlyFilled.Count > 0);
		}

		private static string CreateVmt(string baseTexture, MipTexture texture, bool isAlphaTested, bool animated, BlendMode blendMode, float amount,
			float scrollSpeed)
		{
			// Water isn't lightmapped in GoldSrc, and neither are translucent or additive brush entities
			var isUnlit = IsTurbulent(GetBaseName(texture.Name)) || blendMode != BlendMode.Opaque;
			var shader = isUnlit ? "UnlitGeneric" : "LightmappedGeneric";

			var vmt = new StringBuilder();
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\"{shader}\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{baseTexture}\"");
			// Texture coordinates are in texels of the original texture
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingwidth\" \"{texture.Width}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingheight\" \"{texture.Height}\"");
			switch (blendMode)
			{
				case BlendMode.Opaque:
					if (isAlphaTested)
						vmt.AppendLine("\t\"$alphatest\" \"1\"");
					break;
				case BlendMode.Translucent:
					// Blends by the texture's alpha too, so '{' texels stay transparent
					vmt.AppendLine("\t\"$translucent\" \"1\"");
					if (amount < 1f)
						vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$alpha\" \"{amount:0.###}\"");
					break;
				case BlendMode.Additive:
					// renderamt scales how much is added
					vmt.AppendLine("\t\"$additive\" \"1\"");
					if (isAlphaTested)
						vmt.AppendLine("\t\"$alphatest\" \"1\"");
					if (amount < 1f)
						vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$color\" \"[{amount:0.###} {amount:0.###} {amount:0.###}]\"");
					break;
			}
			if (animated || scrollSpeed != 0f)
			{
				vmt.AppendLine("\t\"Proxies\"");
				vmt.AppendLine("\t{");
				if (animated)
				{
					vmt.AppendLine("\t\t\"AnimatedTexture\"");
					vmt.AppendLine("\t\t{");
					vmt.AppendLine("\t\t\t\"animatedTextureVar\" \"$basetexture\"");
					vmt.AppendLine("\t\t\t\"animatedTextureFrameNumVar\" \"$frame\"");
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"animatedTextureFrameRate\" \"{AnimationFrameRate}\"");
					vmt.AppendLine("\t\t}");
				}
				if (scrollSpeed != 0f)
				{
					// GoldSrc moves the texture coordinates back along S by the speed in texels per second (forward when
					// the conveyor runs backwards), whatever the texture's scale on the face
					vmt.AppendLine("\t\t\"TextureScroll\"");
					vmt.AppendLine("\t\t{");
					vmt.AppendLine("\t\t\t\"textureScrollVar\" \"$basetexturetransform\"");
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"textureScrollRate\" \"{MathF.Abs(scrollSpeed) / texture.Width:0.#####}\"");
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"textureScrollAngle\" \"{(scrollSpeed >= 0f ? 180 : 0)}\"");
					vmt.AppendLine("\t\t}");
				}
				vmt.AppendLine("\t}");
			}
			vmt.AppendLine("}");

			return vmt.ToString();
		}
	}
}
