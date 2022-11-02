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
	}
}
