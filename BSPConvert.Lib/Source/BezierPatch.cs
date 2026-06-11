using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvert.Lib
{
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
		private Vector3[] controlPoints;
		
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
			var bi = QuadraticBezier(u);
			var bj = QuadraticBezier(v);

			// Accumulate in double to prevent JIT FMA fusion in .NET 9+ from changing
			// intermediate rounding and producing different float results per component.
			double rx = 0, ry = 0, rz = 0;
			for (var i = 0; i < 3; i++)
			{
				for (var j = 0; j < 3; j++)
				{
					var w = (double)bi[i] * (double)bj[j];
					var cp = controlPoints[i + j * 3];
					rx += (double)cp.X * w;
					ry += (double)cp.Y * w;
					rz += (double)cp.Z * w;
				}
			}

			return new Vector3((float)rx, (float)ry, (float)rz);
		}

		private double[] QuadraticBezier(float t)
		{
			var dt = (double)t;
			return new double[3]
			{
				(1.0 - dt) * (1.0 - dt),
				2.0 * dt * (1.0 - dt),
				dt * dt
			};
		}
	}
}
