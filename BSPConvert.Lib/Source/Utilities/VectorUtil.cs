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

		// Component-wise double-precision lerp — avoids FMA rounding differences in .NET 9+.
		public static Vector3 LerpDouble(Vector3 a, Vector3 b, float t)
		{
			var dt = (double)t;
			return new Vector3(
				(float)((double)a.X + dt * ((double)b.X - (double)a.X)),
				(float)((double)a.Y + dt * ((double)b.Y - (double)a.Y)),
				(float)((double)a.Z + dt * ((double)b.Z - (double)a.Z)));
		}

		// Double-precision normalize — avoids JIT SIMD differences in .NET 9+ affecting sqrt/divide.
		// Returns the normalized vector and the original magnitude via the out parameter.
		public static Vector3 NormalizeDouble(Vector3 v, out float magnitude)
		{
			var magSq = (double)v.X * (double)v.X
					  + (double)v.Y * (double)v.Y
					  + (double)v.Z * (double)v.Z;
			var magDouble = Math.Sqrt(magSq);
			magnitude = (float)magDouble;

			return magDouble > 0.0
				? new Vector3(
					(float)((double)v.X / magDouble),
					(float)((double)v.Y / magDouble),
					(float)((double)v.Z / magDouble))
				: Vector3.Zero;
		}
	}
}