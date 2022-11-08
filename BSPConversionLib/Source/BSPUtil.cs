using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public static class BSPUtil
	{
		public static int GetHashCode(TextureInfo textureInfo)
		{
			return (textureInfo.UAxis, textureInfo.VAxis, textureInfo.LightmapUAxis, textureInfo.LightmapVAxis, textureInfo.TextureIndex).GetHashCode();
		}

		public static NumList CreateNumList<T>(T[] array, NumList.DataType dataType, BSP bsp = null)
		{
			var size = GetDataTypeSize(dataType);
			var bytes = new byte[array.Length * size];
			Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
			return new NumList(bytes, dataType, bsp);
		}

		private static int GetDataTypeSize(NumList.DataType dataType)
		{
			switch (dataType)
			{
				case NumList.DataType.SByte:
				case NumList.DataType.Byte:
					return 1;
				case NumList.DataType.Int16:
				case NumList.DataType.UInt16:
					return 2;
				case NumList.DataType.Int32:
				case NumList.DataType.UInt32:
					return 4;
				case NumList.DataType.Int64:
					return 8;
				default:
					return 0;
			}
		}
	}
}
