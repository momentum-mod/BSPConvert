#if UNITY
	using Vector3 = UnityEngine.Vector3;
	using Vector2 = UnityEngine.Vector2;
#elif GODOT
	using Vector3 = Godot.Vector3;
	using Vector2 = Godot.Vector2;
#elif NEOAXIS
	using Vector3 = NeoAxis.Vector3F;
	using Vector2 = NeoAxis.Vector2F;
#else
	using Vector3 = System.Numerics.Vector3;
	using Vector2 = System.Numerics.Vector2;
#endif

namespace BSPConvert.Lib
{
	public static class VectorUtil
	{
		public static bool IsValid(this Vector2 vec)
		{
			return float.IsFinite(vec.X) && float.IsFinite(vec.Y);
		}

		public static bool IsValid(this Vector3 vec)
		{
			return float.IsFinite(vec.X) && float.IsFinite(vec.Y) && float.IsFinite(vec.Z);
		}
	}
}