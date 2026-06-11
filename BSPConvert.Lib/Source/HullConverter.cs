using LibBSP;
using System;
using System.Collections.Generic;

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

	public class HullConverter
	{
		/// <summary>
		/// Converts the specified face vertices into a convex polygonal hull using the gift wrapping algorithm
		/// </summary>
		public static List<Vertex> ConvertConvexHull(Vertex[] faceVerts, Vector3 faceNormal)
		{
			// Treat face vertices as an arbitrary set of points on a plane and use the gift wrapping algorithm to generate a convex polygon
			var hullVerts = new List<Vertex>();

			var pointOnHull = GetStartingVertex(faceVerts);
			Vertex endPoint;
			do
			{
				hullVerts.Add(pointOnHull);
				endPoint = faceVerts[0];
				for (var j = 1; j < faceVerts.Length; j++)
				{
					if (endPoint.position == pointOnHull.position || IsLeftOfLine(pointOnHull, endPoint, faceVerts[j], faceNormal))
						endPoint = faceVerts[j];
				}

				pointOnHull = endPoint;
			}
			while (endPoint.position != hullVerts[0].position && hullVerts.Count < faceVerts.Length);

			return hullVerts;
		}

		// Returns the lexicographically smallest vertex, which is guaranteed to be on the convex hull
		private static Vertex GetStartingVertex(Vertex[] faceVerts)
		{
			var start = faceVerts[0];
			for (var i = 1; i < faceVerts.Length; i++)
			{
				var p = faceVerts[i].position;
				var s = start.position;
				if (p.X < s.X ||
					(p.X == s.X && p.Y < s.Y) ||
					(p.X == s.X && p.Y == s.Y && p.Z < s.Z))
				{
					start = faceVerts[i];
				}
			}
			return start;
		}

		private static bool IsLeftOfLine(Vertex pointOnHull, Vertex endPoint, Vertex vertex, Vector3 faceNormal)
		{
			var a = endPoint.position - pointOnHull.position;
			var b = vertex.position - pointOnHull.position;
			var cross = Vector3.Cross(a, b);
			var dot = Vector3.Dot(cross, faceNormal);

			if (dot != 0f)
				return dot > 0f;

			// Collinear: prefer the farther vertex so the hull doesn't prematurely close
			return Vector3.Dot(b, b) > Vector3.Dot(a, a);
		}
	}
}
