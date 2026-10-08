using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BSPConvert.Lib
{
	// A plane's inside half-space: Dot(normal, p) <= dist
	public readonly record struct HalfSpace(Vector3 Normal, float Dist);

	// A convex region bounded by half-spaces, reduced to the planes that actually form a face of it. Used to turn
	// the half-spaces collected along a BSP tree path (which include many planes that don't touch the leaf) into
	// brushes. Face windings are computed in double precision with the Quake-style winding clip.
	public class ConvexRegion
	{
		private const double ClipEpsilon = 0.001;
		private const double MinFaceArea = 1e-6;
		private const double BaseWindingSize = 262144.0;

		// Half-spaces that bound the region with a face of non-zero area
		public IReadOnlyList<HalfSpace> Faces { get; }
		// Each face's polygon, wound clockwise seen from outside the region
		public IReadOnlyList<IReadOnlyList<Vector3>> Windings { get; }
		// Corner points of the region (deduplicated face winding points)
		public IReadOnlyList<Vector3> Vertices { get; }
		public Vector3 Mins { get; }
		public Vector3 Maxs { get; }

		private ConvexRegion(List<HalfSpace> faces, List<IReadOnlyList<Vector3>> windings, List<Vector3> vertices)
		{
			Faces = faces;
			Windings = windings;
			Vertices = vertices;

			var mins = new Vector3(float.MaxValue);
			var maxs = new Vector3(float.MinValue);
			foreach (var v in vertices)
			{
				mins = Vector3.Min(mins, v);
				maxs = Vector3.Max(maxs, v);
			}

			Mins = mins;
			Maxs = maxs;
		}

		// Returns null when the half-spaces enclose no volume
		public static ConvexRegion? Create(IEnumerable<HalfSpace> halfSpaces)
		{
			var planes = halfSpaces.Distinct().ToList();
			var faces = new List<HalfSpace>();
			var windings = new List<IReadOnlyList<Vector3>>();
			var vertices = new List<Vector3>();
			var vertexSet = new HashSet<Vector3>();

			for (var i = 0; i < planes.Count; i++)
			{
				var winding = BaseWinding(planes[i]);
				for (var j = 0; j < planes.Count && winding.Count > 0; j++)
				{
					if (j != i)
						winding = Clip(winding, planes[j]);
				}

				if (winding.Count < 3 || Area(winding) < MinFaceArea)
					continue;

				faces.Add(planes[i]);
				var points = new List<Vector3>(winding.Count);
				foreach (var point in winding)
				{
					var v = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
					points.Add(v);
					if (vertexSet.Add(v))
						vertices.Add(v);
				}
				windings.Add(points);
			}

			// A closed convex volume needs at least 4 faces
			if (faces.Count < 4)
				return null;

			return new ConvexRegion(faces, windings, vertices);
		}

		// Which side(s) of a plane the region's corners are on, ignoring corners within epsilon of the plane
		public (bool front, bool back) Classify(Vector3 normal, float dist, float epsilon)
		{
			var front = false;
			var back = false;
			foreach (var v in Vertices)
			{
				var d = Vector3.Dot(normal, v) - dist;
				if (d > epsilon)
					front = true;
				else if (d < -epsilon)
					back = true;

				if (front && back)
					break;
			}

			return (front, back);
		}

		// A large quad lying on the plane, wound so its normal matches the plane's
		private static List<Vector3D> BaseWinding(HalfSpace plane)
		{
			var normal = new Vector3D(plane.Normal.X, plane.Normal.Y, plane.Normal.Z);

			// Pick the axis least aligned with the normal as the reference up vector
			var ax = Math.Abs(normal.X);
			var ay = Math.Abs(normal.Y);
			var az = Math.Abs(normal.Z);
			var up = az >= ax && az >= ay ? new Vector3D(1, 0, 0) : new Vector3D(0, 0, 1);

			up = (up - normal * Vector3D.Dot(up, normal)).Normalized();
			var right = Vector3D.Cross(up, normal);

			var origin = normal * plane.Dist;
			up *= BaseWindingSize;
			right *= BaseWindingSize;

			return new List<Vector3D>
			{
				origin - right + up,
				origin + right + up,
				origin + right - up,
				origin - right - up
			};
		}

		// Keeps the part of the winding inside the half-space
		private static List<Vector3D> Clip(List<Vector3D> winding, HalfSpace plane)
		{
			var normal = new Vector3D(plane.Normal.X, plane.Normal.Y, plane.Normal.Z);
			var dists = new double[winding.Count];
			var anyOutside = false;
			var anyInside = false;
			for (var i = 0; i < winding.Count; i++)
			{
				dists[i] = Vector3D.Dot(winding[i], normal) - plane.Dist;
				if (dists[i] > ClipEpsilon)
					anyOutside = true;
				else if (dists[i] < -ClipEpsilon)
					anyInside = true;
			}

			if (!anyOutside)
				return winding;
			if (!anyInside)
				return new List<Vector3D>();

			var result = new List<Vector3D>(winding.Count + 1);
			for (var i = 0; i < winding.Count; i++)
			{
				var p1 = winding[i];
				var d1 = dists[i];
				if (d1 <= ClipEpsilon)
					result.Add(p1);

				var next = (i + 1) % winding.Count;
				var d2 = dists[next];
				if ((d1 > ClipEpsilon && d2 < -ClipEpsilon) || (d1 < -ClipEpsilon && d2 > ClipEpsilon))
				{
					var t = d1 / (d1 - d2);
					result.Add(p1 + (winding[next] - p1) * t);
				}
			}

			return result;
		}

		private static double Area(List<Vector3D> winding)
		{
			var total = new Vector3D(0, 0, 0);
			for (var i = 2; i < winding.Count; i++)
				total += Vector3D.Cross(winding[i - 1] - winding[0], winding[i] - winding[0]);

			return total.Length() * 0.5;
		}

		private readonly record struct Vector3D(double X, double Y, double Z)
		{
			public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
			public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
			public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
			public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
			public static Vector3D Cross(Vector3D a, Vector3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
			public double Length() => Math.Sqrt(Dot(this, this));
			public Vector3D Normalized() => this * (1.0 / Length());
		}
	}
}
