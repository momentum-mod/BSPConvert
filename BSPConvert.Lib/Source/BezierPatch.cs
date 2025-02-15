namespace BSPConvert.Lib;
using System;

#if UNITY
	using Vector3 = UnityEngine.Vector3;
#elif GODOT
	using Vector3 = Godot.Vector3;
#elif NEOAXIS
	using Vector3 = NeoAxis.Vector3F;
#else
using Vector3 = System.Numerics.Vector3;

#endif

public class BezierPatch
	{
		private readonly Vector3[] controlPoints;
		
		public BezierPatch(Vector3[] controlPoints)
		{
			if (controlPoints.Length != 9)
				throw new ArgumentException("Invalid patch control point count");

			this.controlPoints = controlPoints;
		}
		
		/// <summary>
		/// Returns a point along the quadratic bezier patch
		/// </summary>
		/// <param name="u">[0-1] fraction along the width of the patch</param>
		/// <param name="v">[0-1] fraction along the height of the patch</param>
		public Vector3 GetPoint(float u, float v)
		{
        float[] bi = QuadraticBezier(u);
        float[] bj = QuadraticBezier(v);

			var result = new Vector3(0f, 0f, 0f);
			for (int i = 0; i < 3; i++)
			{
				for (int j = 0; j < 3; j++)
				{
					result += controlPoints[i + (j * 3)] * bi[i] * bj[j];
				}
			}

			return result;
		}

    private float[] QuadraticBezier(float t) =>
        [
                (1f - t) * (1f - t),
                2f * t * (1f - t),
                t * t
        ];
}
