using LibBSP;
using System;
using System.Collections.Generic;

namespace BSPConversionLib
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
			// Treat face vertices as an arbitrary set of points on a plane and use the gift wrapping alogrithm to generate a convex polygon
			var hullVerts = new List<Vertex>();

			// TODO: Find initial point on hull
			var pointOnHull = faceVerts[0];
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

		private static bool IsLeftOfLine(Vertex pointOnHull, Vertex endPoint, Vertex vertex, Vector3 faceNormal)
		{
			var a = endPoint.position - pointOnHull.position;
			var b = vertex.position - pointOnHull.position;
			var cross = Vector3.Cross(a, b);

			// Use face normal to determine if the vertex is on the left side of the line
			// TODO: Handle colinear vertices (dot product should be 0)
			return Vector3.Dot(cross, faceNormal) > 0;
		}
	}
}
