namespace BSPConvert.Lib;
using LibBSP;

public static class BSPUtil
	{
    public static int GetHashCode(TextureInfo textureInfo) => (textureInfo.UAxis, textureInfo.VAxis, textureInfo.LightmapUAxis, textureInfo.LightmapVAxis, textureInfo.TextureIndex).GetHashCode();
}
