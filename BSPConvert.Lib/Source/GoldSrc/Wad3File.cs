using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace BSPConvert.Lib.GoldSrc
{
	// A WAD3 texture archive (Half-Life). Only the lump directory is read up front; textures are read on demand.
	public class Wad3File
	{
		private const int LumpInfoSize = 32; // filepos, disksize, size, type, compression, pad[2], name[16]
		private const byte LumpTypeMipTex = 0x43;

		private readonly Dictionary<string, (int offset, int size)> mipTexLumps = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

		public string FilePath { get; }

		private Wad3File(string filePath)
		{
			FilePath = filePath;
		}

		// Returns null if the file isn't a readable WAD3
		public static Wad3File? Open(string filePath)
		{
			try
			{
				using var stream = File.OpenRead(filePath);
				Span<byte> header = stackalloc byte[12];
				if (stream.Read(header) != header.Length || header[0] != 'W' || header[1] != 'A' || header[2] != 'D' || header[3] != '3')
					return null;

				var lumpCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
				var directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8));
				if (lumpCount < 0 || directoryOffset < 0 || directoryOffset + (long)lumpCount * LumpInfoSize > stream.Length)
					return null;

				var directory = new byte[lumpCount * LumpInfoSize];
				stream.Position = directoryOffset;
				stream.ReadExactly(directory);

				var wad = new Wad3File(filePath);
				for (var i = 0; i < lumpCount; i++)
				{
					var info = directory.AsSpan(i * LumpInfoSize, LumpInfoSize);
					var offset = BinaryPrimitives.ReadInt32LittleEndian(info);
					var diskSize = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(4));
					var type = info[12];
					var compression = info[13];
					if (type != LumpTypeMipTex || compression != 0 || offset < 0 || offset + (long)diskSize > stream.Length)
						continue;

					// First lump wins, like the engine's lookup
					wad.mipTexLumps.TryAdd(MipTexture.ReadName(info.Slice(16)), (offset, diskSize));
				}

				return wad;
			}
			catch (IOException)
			{
				return null;
			}
			catch (UnauthorizedAccessException)
			{
				return null;
			}
		}

		public bool Contains(string textureName)
		{
			return mipTexLumps.ContainsKey(textureName);
		}

		public MipTexture? ReadMipTexture(string textureName)
		{
			if (!mipTexLumps.TryGetValue(textureName, out var lump))
				return null;

			using var stream = File.OpenRead(FilePath);
			var data = new byte[lump.size];
			stream.Position = lump.offset;
			stream.ReadExactly(data);

			return MipTexture.Read(data);
		}
	}
}
