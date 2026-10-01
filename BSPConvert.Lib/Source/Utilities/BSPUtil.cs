using LibBSP;

namespace BSPConvert.Lib
{
	// Identity of a texinfo for deduplication: every field the texinfo lump stores
	public readonly struct TextureInfoKey : IEquatable<TextureInfoKey>
	{
		public readonly float UX, UY, UZ, UOffset;
		public readonly float VX, VY, VZ, VOffset;
		public readonly float LmUX, LmUY, LmUZ, LmUOffset;
		public readonly float LmVX, LmVY, LmVZ, LmVOffset;
		public readonly int TextureIndex;
		public readonly int Flags;

		public TextureInfoKey(TextureInfo textureInfo)
		{
			var u = textureInfo.UAxis;
			var v = textureInfo.VAxis;
			var translation = textureInfo.Translation;
			UX = u.X(); UY = u.Y(); UZ = u.Z(); UOffset = translation.X;
			VX = v.X(); VY = v.Y(); VZ = v.Z(); VOffset = translation.Y;

			var lmU = textureInfo.LightmapUAxis;
			var lmV = textureInfo.LightmapVAxis;
			var lmTranslation = textureInfo.LightmapTranslation;
			LmUX = lmU.X(); LmUY = lmU.Y(); LmUZ = lmU.Z(); LmUOffset = lmTranslation.X;
			LmVX = lmV.X(); LmVY = lmV.Y(); LmVZ = lmV.Z(); LmVOffset = lmTranslation.Y;

			TextureIndex = textureInfo.TextureIndex;
			Flags = textureInfo.Flags;
		}

		public bool Equals(TextureInfoKey other) =>
			UX == other.UX && UY == other.UY && UZ == other.UZ && UOffset == other.UOffset &&
			VX == other.VX && VY == other.VY && VZ == other.VZ && VOffset == other.VOffset &&
			LmUX == other.LmUX && LmUY == other.LmUY && LmUZ == other.LmUZ && LmUOffset == other.LmUOffset &&
			LmVX == other.LmVX && LmVY == other.LmVY && LmVZ == other.LmVZ && LmVOffset == other.LmVOffset &&
			TextureIndex == other.TextureIndex && Flags == other.Flags;

		public override bool Equals(object? obj) => obj is TextureInfoKey other && Equals(other);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(UX); hash.Add(UY); hash.Add(UZ); hash.Add(UOffset);
			hash.Add(VX); hash.Add(VY); hash.Add(VZ); hash.Add(VOffset);
			hash.Add(LmUX); hash.Add(LmUY); hash.Add(LmUZ); hash.Add(LmUOffset);
			hash.Add(LmVX); hash.Add(LmVY); hash.Add(LmVZ); hash.Add(LmVOffset);
			hash.Add(TextureIndex);
			hash.Add(Flags);
			return hash.ToHashCode();
		}
	}

	public static class BSPUtil
	{
	}
}
