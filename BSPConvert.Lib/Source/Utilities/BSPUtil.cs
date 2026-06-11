using LibBSP;

namespace BSPConvert.Lib
{
	public readonly struct TextureInfoKey : IEquatable<TextureInfoKey>
	{
		public readonly float UX, UY, UZ;
		public readonly float VX, VY, VZ;
		public readonly int TextureIndex;
		public readonly int Flags;

		public TextureInfoKey(TextureInfo textureInfo)
		{
			var u = textureInfo.UAxis;
			var v = textureInfo.VAxis;
			UX = u.X(); UY = u.Y(); UZ = u.Z();
			VX = v.X(); VY = v.Y(); VZ = v.Z();
			TextureIndex = textureInfo.TextureIndex;
			Flags = textureInfo.Flags;
		}

		public bool Equals(TextureInfoKey other) =>
			UX == other.UX && UY == other.UY && UZ == other.UZ &&
			VX == other.VX && VY == other.VY && VZ == other.VZ &&
			TextureIndex == other.TextureIndex && Flags == other.Flags;

		public override bool Equals(object? obj) => obj is TextureInfoKey other && Equals(other);

		public override int GetHashCode() =>
			HashCode.Combine(UX, UY, UZ, VX, VY, VZ, TextureIndex, Flags);
	}

	public static class BSPUtil
	{
	}
}
