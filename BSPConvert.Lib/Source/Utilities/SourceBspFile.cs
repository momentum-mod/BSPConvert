using LibBSP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BSPConvert.Lib
{
	// A Source BSP file whose lumps are read and replaced as raw bytes, for changing a few lumps of a compiled map
	// without rebuilding the rest. Replaced lumps are appended to the end of the file and the header points at them;
	// every other lump stays where it was, byte for byte, which keeps lumps that store file offsets (the game lump)
	// and lumps BSPConvert doesn't parse intact.
	public class SourceBspFile
	{
		public const int HeaderLumps = 64;
		private const int HeaderSize = 8 + HeaderLumps * 16 + 4;
		private const int Ident = 'V' | 'B' << 8 | 'S' << 16 | 'P' << 24;

		private readonly byte[] data;
		private readonly (int offset, int length, int version, int uncompressedSize)[] lumps = new (int, int, int, int)[HeaderLumps];
		private readonly Dictionary<int, (byte[] data, int version)> replacedLumps = new Dictionary<int, (byte[], int)>();

		public int Version { get; }

		private SourceBspFile(byte[] data)
		{
			this.data = data;
			Version = BitConverter.ToInt32(data, 4);
			for (var i = 0; i < HeaderLumps; i++)
			{
				var offset = 8 + i * 16;
				lumps[i] = (BitConverter.ToInt32(data, offset), BitConverter.ToInt32(data, offset + 4), BitConverter.ToInt32(data, offset + 8), BitConverter.ToInt32(data, offset + 12));
			}
		}

		// Returns null if the file isn't a Source BSP
		public static SourceBspFile? Read(string path)
		{
			var data = File.ReadAllBytes(path);
			if (data.Length < HeaderSize || BitConverter.ToInt32(data, 0) != Ident)
				return null;

			return new SourceBspFile(data);
		}

		public int GetLumpVersion(int index)
		{
			return replacedLumps.TryGetValue(index, out var replaced) ? replaced.version : lumps[index].version;
		}

		// The lump's bytes, decompressed if the map was compressed
		public byte[] GetLump(int index)
		{
			if (replacedLumps.TryGetValue(index, out var replaced))
				return replaced.data;

			var (offset, length, _, uncompressedSize) = lumps[index];
			if (length <= 0 || offset < 0 || offset + length > data.Length)
				return Array.Empty<byte>();

			var lump = new byte[length];
			Buffer.BlockCopy(data, offset, lump, 0, length);
			if (uncompressedSize > 0 && Lzma.IsCompressed(lump))
				lump = Lzma.Decompress(lump);

			return lump;
		}

		public string GetEntities()
		{
			return Encoding.Latin1.GetString(GetLump(0)).TrimEnd('\0');
		}

		public void SetLump(int index, byte[] lumpData, int version)
		{
			replacedLumps[index] = (lumpData, version);
		}

		public void SetEntities(string entities)
		{
			SetLump(0, Encoding.Latin1.GetBytes(entities + "\0"), GetLumpVersion(0));
		}

		public void Write(string path)
		{
			using var output = new MemoryStream();
			output.Write(data, 0, data.Length);

			var header = (byte[])data.Clone();
			foreach (var (index, (lumpData, version)) in replacedLumps)
			{
				// Lumps start on 4-byte boundaries
				while (output.Length % 4 != 0)
					output.WriteByte(0);

				var offset = (int)output.Length;
				output.Write(lumpData, 0, lumpData.Length);

				var headerOffset = 8 + index * 16;
				BitConverter.GetBytes(offset).CopyTo(header, headerOffset);
				BitConverter.GetBytes(lumpData.Length).CopyTo(header, headerOffset + 4);
				BitConverter.GetBytes(version).CopyTo(header, headerOffset + 8);
				BitConverter.GetBytes(0).CopyTo(header, headerOffset + 12);
			}

			var bytes = output.ToArray();
			Buffer.BlockCopy(header, 0, bytes, 0, HeaderSize);
			File.WriteAllBytes(path, bytes);
		}
	}
}
