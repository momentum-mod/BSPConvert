using sourcepp.vtfpp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// Converts GoldSrc miptextures to a VTF and VMT each, written under the content directory as
	// <contentDir>/<materialName>.vtf/.vmt (the layout SourceBspBuilder reads texdata from).
	public class GoldSrcMaterialConverter
	{
		// Palette index drawn transparent on '{' textures
		private const byte TransparentIndex = 255;

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
			var basePath = Path.Combine(contentDir, materialName.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

			var isAlphaTested = texture.Name.StartsWith('{');
			var pixels = DecodeRGBA(texture, isAlphaTested);

			var vtfPath = basePath + ".vtf";
			if (!VTF.Create(pixels, ImageFormat.RGBA8888, (ushort)texture.Width, (ushort)texture.Height, vtfPath, VtfOptions))
				return false;

			var vmtPath = basePath + ".vmt";
			File.WriteAllText(vmtPath, CreateVmt(materialName, texture, isAlphaTested));

			writtenFiles.Add(vtfPath);
			writtenFiles.Add(vmtPath);
			return true;
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

		private static string CreateVmt(string materialName, MipTexture texture, bool isAlphaTested)
		{
			// Water ('!') isn't lightmapped in GoldSrc
			var shader = texture.Name.StartsWith('!') ? "UnlitGeneric" : "LightmappedGeneric";

			var vmt = new StringBuilder();
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\"{shader}\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			// Texture coordinates are in texels of the original texture
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingwidth\" \"{texture.Width}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingheight\" \"{texture.Height}\"");
			if (isAlphaTested)
				vmt.AppendLine("\t\"$alphatest\" \"1\"");
			vmt.AppendLine("}");

			return vmt.ToString();
		}
	}
}
