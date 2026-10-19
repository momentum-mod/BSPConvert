using System;
using System.Collections.Generic;
using System.Linq;

namespace BSPConvert.Lib.Zones
{
	public readonly record struct Point2(double X, double Y)
	{
		public static Point2 operator +(Point2 a, Point2 b) => new Point2(a.X + b.X, a.Y + b.Y);
		public static Point2 operator -(Point2 a, Point2 b) => new Point2(a.X - b.X, a.Y - b.Y);
		public static Point2 operator *(Point2 a, double s) => new Point2(a.X * s, a.Y * s);

		public double Length => Math.Sqrt(X * X + Y * Y);
		public double DistanceTo(Point2 other) => (this - other).Length;
	}

	/// <summary>
	/// 2D polygon helpers for zone regions. A region must pass Momentum's ValidateRegionPolygon
	/// (mom_zone_runtime_data.cpp): 3 to 64 points, every pair of points at least 4 units apart, every corner
	/// angle at least 15 degrees and short of a straight line, and no self-intersections.
	/// </summary>
	public static class RegionPolygon
	{
		public const int MaxPoints = 64;
		public const double MinPointDistance = 4.0;
		public const double MinAngle = 15.0 * Math.PI / 180.0;

		// Cleanup aims a little past the engine's limits so float rounding in the zone file can't fail validation
		private const double SafePointDistance = MinPointDistance + 0.5;
		private const double SafeMinAngle = MinAngle + 1.0 * Math.PI / 180.0;

		// A corner this close to the line between its neighbours adds nothing to the shape
		private const double ColinearDistance = 0.25;
		private const double ColinearAngle = Math.PI - 0.5 * Math.PI / 180.0;

		/// <summary>
		/// Counter-clockwise convex hull (Andrew's monotone chain). Collinear points are left out.
		/// </summary>
		public static List<Point2> ConvexHull(IEnumerable<Point2> points)
		{
			var sorted = points
				.Distinct()
				.OrderBy(p => p.X)
				.ThenBy(p => p.Y)
				.ToList();

			if (sorted.Count < 3)
				return sorted;

			var lower = HalfHull(sorted);
			sorted.Reverse();
			var upper = HalfHull(sorted);

			// Each half ends with the point the other half starts with
			lower.RemoveAt(lower.Count - 1);
			upper.RemoveAt(upper.Count - 1);
			lower.AddRange(upper);

			return lower;

			static List<Point2> HalfHull(List<Point2> sorted)
			{
				var half = new List<Point2>();
				foreach (var p in sorted)
				{
					while (half.Count >= 2 && Cross(half[^2], half[^1], p) <= 0)
						half.RemoveAt(half.Count - 1);

					half.Add(p);
				}

				return half;
			}
		}

		/// <summary>
		/// Reshapes a counter-clockwise convex polygon so it passes Momentum's validation, changing its outline as
		/// little as possible: drops corners that are nearly straight, merges points that are too close together
		/// and blunts corners that are too sharp. Returns null if the polygon can't be fixed (e.g. a sliver).
		/// </summary>
		public static List<Point2>? Simplify(List<Point2> polygon)
		{
			var points = new List<Point2>(polygon);

			for (var iteration = 0; iteration < 1000; iteration++)
			{
				if (points.Count < 3)
					return null;

				if (RemoveColinearPoint(points) || MergeShortestEdge(points) || BluntSharpestCorner(points))
					continue;

				return IsValid(points) ? points : null;
			}

			return null;
		}

		private static bool RemoveColinearPoint(List<Point2> points)
		{
			for (var i = 0; i < points.Count; i++)
			{
				var prev = points[(i - 1 + points.Count) % points.Count];
				var curr = points[i];
				var next = points[(i + 1) % points.Count];

				if (DistanceToLine(curr, prev, next) < ColinearDistance || CornerAngle(prev, curr, next) > ColinearAngle)
				{
					points.RemoveAt(i);
					return true;
				}
			}

			return false;
		}

		// Midpoint merges keep a convex polygon convex
		private static bool MergeShortestEdge(List<Point2> points)
		{
			var shortest = -1;
			var shortestLength = SafePointDistance;

			for (var i = 0; i < points.Count; i++)
			{
				var length = points[i].DistanceTo(points[(i + 1) % points.Count]);
				if (length < shortestLength)
				{
					shortest = i;
					shortestLength = length;
				}
			}

			if (shortest < 0)
				return false;

			var nextIndex = (shortest + 1) % points.Count;
			var midpoint = (points[shortest] + points[nextIndex]) * 0.5;

			points[shortest] = midpoint;
			points.RemoveAt(nextIndex);
			return true;
		}

		// Cuts the tip off a sharp corner, leaving two corners of 90 + angle/2 degrees each
		private static bool BluntSharpestCorner(List<Point2> points)
		{
			var sharpest = -1;
			var sharpestAngle = SafeMinAngle;

			for (var i = 0; i < points.Count; i++)
			{
				var angle = CornerAngle(points[(i - 1 + points.Count) % points.Count], points[i], points[(i + 1) % points.Count]);
				if (angle < sharpestAngle)
				{
					sharpest = i;
					sharpestAngle = angle;
				}
			}

			if (sharpest < 0)
				return false;

			var prev = points[(sharpest - 1 + points.Count) % points.Count];
			var curr = points[sharpest];
			var next = points[(sharpest + 1) % points.Count];

			// Distance along each edge so the new edge across the tip is long enough
			var cut = (SafePointDistance + 0.1) / (2.0 * Math.Sin(sharpestAngle / 2.0));
			var toPrev = prev - curr;
			var toNext = next - curr;
			if (cut > toPrev.Length - SafePointDistance || cut > toNext.Length - SafePointDistance)
				return false; // Too thin to blunt; IsValid rejects it

			points[sharpest] = curr + toPrev * (cut / toPrev.Length);
			points.Insert(sharpest + 1, curr + toNext * (cut / toNext.Length));
			return true;
		}

		/// <summary>
		/// Mirrors Momentum's ValidateRegionPolygon for a closed polygon, plus a convexity check that stands in
		/// for its self-intersection test (every polygon made here is convex).
		/// </summary>
		public static bool IsValid(IReadOnlyList<Point2> points)
		{
			var count = points.Count;
			if (count < 3 || count > MaxPoints)
				return false;

			for (var i = 0; i < count; i++)
			{
				for (var j = i + 1; j < count; j++)
				{
					if (points[i].DistanceTo(points[j]) < MinPointDistance)
						return false;
				}
			}

			for (var i = 0; i < count; i++)
			{
				var prev = points[(i - 1 + count) % count];
				var curr = points[i];
				var next = points[(i + 1) % count];

				var angle = CornerAngle(prev, curr, next);
				if (angle < MinAngle || angle > Math.PI - 0.0001)
					return false;

				if (Cross(prev, curr, next) <= 0)
					return false;
			}

			return true;
		}

		/// <summary>
		/// An axis-aligned rectangle covering the points, widened to the minimum point distance if needed.
		/// </summary>
		public static List<Point2> BoundingRectangle(IEnumerable<Point2> points)
		{
			var list = points.ToList();
			var minX = list.Min(p => p.X);
			var maxX = list.Max(p => p.X);
			var minY = list.Min(p => p.Y);
			var maxY = list.Max(p => p.Y);

			Widen(ref minX, ref maxX);
			Widen(ref minY, ref maxY);

			return new List<Point2>
			{
				new Point2(minX, maxY),
				new Point2(minX, minY),
				new Point2(maxX, minY),
				new Point2(maxX, maxY)
			};

			static void Widen(ref double min, ref double max)
			{
				var missing = SafePointDistance - (max - min);
				if (missing <= 0)
					return;

				min -= missing / 2;
				max += missing / 2;
			}
		}

		public static double Area(IReadOnlyList<Point2> polygon)
		{
			var area = 0.0;
			for (var i = 0; i < polygon.Count; i++)
			{
				var a = polygon[i];
				var b = polygon[(i + 1) % polygon.Count];
				area += a.X * b.Y - b.X * a.Y;
			}

			return Math.Abs(area) / 2;
		}

		/// <summary>
		/// Intersection of two counter-clockwise convex polygons (Sutherland-Hodgman).
		/// </summary>
		public static List<Point2> Intersect(IReadOnlyList<Point2> subject, IReadOnlyList<Point2> clip)
		{
			var output = subject.ToList();

			for (var i = 0; i < clip.Count && output.Count > 0; i++)
			{
				var edgeStart = clip[i];
				var edgeEnd = clip[(i + 1) % clip.Count];
				var input = output;
				output = new List<Point2>();

				for (var j = 0; j < input.Count; j++)
				{
					var current = input[j];
					var next = input[(j + 1) % input.Count];
					var currentSide = Cross(edgeStart, edgeEnd, current);
					var nextSide = Cross(edgeStart, edgeEnd, next);

					if (currentSide >= 0)
						output.Add(current);

					if ((currentSide >= 0) != (nextSide >= 0))
					{
						var t = currentSide / (currentSide - nextSide);
						output.Add(current + (next - current) * t);
					}
				}
			}

			return output;
		}

		// > 0 when a -> b -> c turns counter-clockwise
		private static double Cross(Point2 a, Point2 b, Point2 c)
		{
			return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
		}

		private static double CornerAngle(Point2 prev, Point2 curr, Point2 next)
		{
			var a = prev - curr;
			var b = next - curr;
			var lengths = a.Length * b.Length;
			if (lengths == 0)
				return 0;

			return Math.Acos(Math.Clamp((a.X * b.X + a.Y * b.Y) / lengths, -1.0, 1.0));
		}

		private static double DistanceToLine(Point2 point, Point2 lineStart, Point2 lineEnd)
		{
			var length = lineStart.DistanceTo(lineEnd);
			if (length == 0)
				return point.DistanceTo(lineStart);

			return Math.Abs(Cross(lineStart, lineEnd, point)) / length;
		}
	}
}
