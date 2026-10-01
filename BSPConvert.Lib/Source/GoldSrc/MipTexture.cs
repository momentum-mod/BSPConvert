using System;
using System.Buffers.Binary;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// A GoldSrc miptex as stored in a BSP's texture lump or a WAD3 lump: an 8-bit paletted image with 4 mip levels,
	// followed by its own 256 color palette.
	public class MipTexture
	{
		public const int HeaderSize = 40; // name[16], width, height, offsets[4]
		public const int PaletteSize = 256 * 3;

		public string Name { get; }
		public int Width { get; }
		public int Height { get; }
		// Mip 0 palette indices, row by row
		public byte[] Pixels { get; }
		// 256 RGB triplets
		public byte[] Palette { get; }

		private MipTexture(string name, int width, int height, byte[] pixels, byte[] palette)
		{
			Name = name;
			Width = width;
			Height = height;
			Pixels = pixels;
			Palette = palette;
		}

		public static string ReadName(ReadOnlySpan<byte> data)
		{
			var nameBytes = data.Slice(0, 16);
			var nameLength = nameBytes.IndexOf((byte)0);
			return Encoding.ASCII.GetString(nameBytes.Slice(0, nameLength < 0 ? 16 : nameLength));
		}

		// Reads a miptex whose header starts at data[0]. Returns null if it has no pixel data (a BSP's reference to a
		// texture that lives in a WAD) or the data is truncated.
		public static MipTexture? Read(ReadOnlySpan<byte> data)
		{
			if (data.Length < HeaderSize)
				return null;

			var name = ReadName(data);
			var width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16));
			var height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(20));
			var mip0Offset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(24));
			var mip3Offset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(36));
			if (width <= 0 || height <= 0 || mip0Offset <= 0 || mip3Offset <= 0)
				return null;

			var pixelCount = width * height;
			// The palette follows the smallest mip, after a 16-bit color count
			var paletteOffset = mip3Offset + (width / 8) * (height / 8) + 2;
			if (mip0Offset + pixelCount > data.Length || paletteOffset + PaletteSize > data.Length)
				return null;

			return new MipTexture(name, width, height,
				data.Slice(mip0Offset, pixelCount).ToArray(),
				data.Slice(paletteOffset, PaletteSize).ToArray());
		}
	}
}
