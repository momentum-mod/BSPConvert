using System;
using System.Collections.Generic;

namespace BSPConvert.Lib.Vis
{
	public readonly record struct Vector3d(double X, double Y, double Z)
	{
		public static readonly Vector3d Zero = new Vector3d(0, 0, 0);

		public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };

		public Vector3d With(int axis, double value) => axis switch
		{
			0 => this with { X = value },
			1 => this with { Y = value },
			_ => this with { Z = value },
		};

		public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

		public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
		public static Vector3d operator -(Vector3d a) => new Vector3d(-a.X, -a.Y, -a.Z);
		public static Vector3d operator *(Vector3d a, double s) => new Vector3d(a.X * s, a.Y * s, a.Z * s);

		public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

		public static Vector3d Cross(Vector3d a, Vector3d b) =>
			new Vector3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

		public Vector3d Normalized()
		{
			var length = Length;
			return length > 0 ? this * (1 / length) : this;
		}
	}

	// A convex polygon, wound so its normal (see PlaneNormal) is its plane's
	public class Winding
	{
		// Half the size of the polygon BaseForPlane starts from, beyond any map's bounds
		private const double MaxWorldCoord = 1 << 20;

		private const int SideFront = 0;
		private const int SideBack = 1;
		private const int SideOn = 2;

		public readonly List<Vector3d> Points;

		public Winding(List<Vector3d> points)
		{
			Points = points;
		}

		// A huge square on the plane
		public static Winding BaseForPlane(Vector3d normal, double distance)
		{
			// The axis the normal is closest to
			var axis = -1;
			var max = -1.0;
			for (var i = 0; i < 3; i++)
			{
				var value = Math.Abs(normal[i]);
				if (value > max)
				{
					axis = i;
					max = value;
				}
			}

			var up = axis == 2 ? new Vector3d(1, 0, 0) : new Vector3d(0, 0, 1);
			up = (up - normal * Vector3d.Dot(up, normal)).Normalized();

			var origin = normal * distance;
			var right = Vector3d.Cross(up, normal) * MaxWorldCoord;
			up *= MaxWorldCoord;

			return new Winding(new List<Vector3d>
			{
				origin - right + up,
				origin + right + up,
				origin + right - up,
				origin - right - up,
			});
		}

		// vvis's PlaneFromWinding normal of the polygon starting with a, b, c
		public static Vector3d PlaneNormal(Vector3d a, Vector3d b, Vector3d c) => PlaneNormalUnnormalized(a, b, c).Normalized();

		public static Vector3d PlaneNormalUnnormalized(Vector3d a, Vector3d b, Vector3d c) => Vector3d.Cross(a - b, c - b);

		// The part in front of the plane, or null if there's none
		public Winding? Chop(Vector3d normal, double distance, double epsilon)
		{
			Split(normal, distance, epsilon, out var front, out _, keepBack: false);
			return front;
		}

		// The parts in front of and behind the plane. A polygon on the plane goes behind it.
		public void Split(Vector3d normal, double distance, double epsilon, out Winding? front, out Winding? back) =>
			Split(normal, distance, epsilon, out front, out back, keepBack: true);

		private void Split(Vector3d normal, double distance, double epsilon, out Winding? front, out Winding? back, bool keepBack)
		{
			var count = Points.Count;
			var dists = new double[count + 1];
			var sides = new int[count + 1];
			var counts = new int[3];
			for (var i = 0; i < count; i++)
			{
				var dist = Vector3d.Dot(Points[i], normal) - distance;
				dists[i] = dist;
				sides[i] = dist > epsilon ? SideFront : dist < -epsilon ? SideBack : SideOn;
				counts[sides[i]]++;
			}
			dists[count] = dists[0];
			sides[count] = sides[0];

			front = null;
			back = null;
			if (counts[SideFront] == 0)
			{
				// Chopping drops a polygon on the plane
				if (keepBack)
					back = this;
				return;
			}
			if (counts[SideBack] == 0)
			{
				front = this;
				return;
			}

			var frontPoints = new List<Vector3d>(count + 4);
			var backPoints = keepBack ? new List<Vector3d>(count + 4) : null;
			for (var i = 0; i < count; i++)
			{
				var point = Points[i];
				if (sides[i] == SideOn)
				{
					frontPoints.Add(point);
					backPoints?.Add(point);
					continue;
				}

				if (sides[i] == SideFront)
					frontPoints.Add(point);
				else
					backPoints?.Add(point);

				if (sides[i + 1] == SideOn || sides[i + 1] == sides[i])
					continue;

				var next = Points[(i + 1) % count];
				var t = dists[i] / (dists[i] - dists[i + 1]);
				var mid = Vector3d.Zero;
				for (var axis = 0; axis < 3; axis++)
				{
					// Exact where the plane is axial
					var value = normal[axis] == 1 ? distance : normal[axis] == -1 ? -distance : point[axis] + t * (next[axis] - point[axis]);
					mid = mid.With(axis, value);
				}

				frontPoints.Add(mid);
				backPoints?.Add(mid);
			}

			front = new Winding(frontPoints);
			if (backPoints != null)
				back = new Winding(backPoints);
		}

		// Whether it has fewer than three edges longer than edgeLength
		public bool IsTiny(double edgeLength)
		{
			var edges = 0;
			for (var i = 0; i < Points.Count; i++)
			{
				var next = Points[(i + 1) % Points.Count];
				if ((next - Points[i]).Length > edgeLength && ++edges == 3)
					return false;
			}

			return true;
		}

		// The corners, without points on the edges between them or repeated points
		public List<Vector3d> WithoutColinearPoints()
		{
			const double epsilon = 0.001;

			var distinct = new List<Vector3d>(Points.Count);
			foreach (var point in Points)
			{
				if (distinct.Count == 0 || (point - distinct[^1]).Length >= epsilon)
					distinct.Add(point);
			}
			while (distinct.Count > 1 && (distinct[0] - distinct[^1]).Length < epsilon)
				distinct.RemoveAt(distinct.Count - 1);

			var result = new List<Vector3d>(distinct.Count);
			var count = distinct.Count;
			for (var i = 0; i < count; i++)
			{
				var point = distinct[i];
				var toPrevious = (distinct[(i + count - 1) % count] - point).Normalized();
				var toNext = (distinct[(i + 1) % count] - point).Normalized();

				// Straight through: the edges are anti-parallel
				if (Vector3d.Dot(toPrevious, toNext) < -1 + epsilon * epsilon)
					continue;

				result.Add(point);
			}

			return result;
		}
	}
}
