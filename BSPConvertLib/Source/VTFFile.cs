using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class VTFFile
	{
		public class Header
		{
			public string signature;
			public uint[] version = new uint[2];
			public uint headerSize;
			public ushort width;
			public ushort height;
			public uint flags;
			public ushort frames;
			public ushort firstFrame;
			public byte[] padding0;
			public float reflectivityX;
			public float reflectivityY;
			public float reflectivityZ;
			public byte[] padding1;
			public float bumpmapScale;
			public uint highResImageFormat;
			public byte mipmapCount;
			public byte lowResImageFormat;
			public byte lowResImageWidth;
			public byte lowResImageHeight;

			public static Header Deserialize(BinaryReader reader)
			{
				var header = new Header();

				header.signature = reader.ReadChars(4).ToString();
				header.version[0] = reader.ReadUInt32();
				header.version[1] = reader.ReadUInt32();
				header.headerSize = reader.ReadUInt32();
				header.width = reader.ReadUInt16();
				header.height = reader.ReadUInt16();
				header.flags = reader.ReadUInt32();
				header.frames = reader.ReadUInt16();
				header.firstFrame = reader.ReadUInt16();
				header.padding0 = reader.ReadBytes(4);
				header.reflectivityX = reader.ReadSingle();
				header.reflectivityY = reader.ReadSingle();
				header.reflectivityZ = reader.ReadSingle();
				header.padding1 = reader.ReadBytes(4);
				header.bumpmapScale = reader.ReadSingle();
				header.highResImageFormat = reader.ReadUInt32();
				header.mipmapCount = reader.ReadByte();
				header.lowResImageFormat = reader.ReadByte();
				header.lowResImageWidth = reader.ReadByte();
				header.lowResImageHeight = reader.ReadByte();

				return header;
			}
		}

		public Header header;

		public static VTFFile Deserialize(BinaryReader reader)
		{
			var vtfFile = new VTFFile();

			vtfFile.header = Header.Deserialize(reader);

			return vtfFile;
		}
	}
}
