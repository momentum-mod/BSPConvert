using BSPConvert.Lib.Zones;

namespace BSPConvert.Test
{
	public class RegionPolygonTests
	{
		[Test]
		public void ConvexHull_IsCounterClockwiseWithoutInnerOrCollinearPoints()
		{
			var hull = RegionPolygon.ConvexHull(new[]
			{
				new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10),
				new Point2(5, 5), new Point2(5, 0), new Point2(0, 0)
			});

			Assert.That(hull, Is.EqualTo(new[] { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10) }));
		}

		// The outline the old clipper made for kool_woodtory's checkpoint *44 (a slanted strip): q3map2's axial bevel
		// planes touch its corners, leaving pairs of points a fraction of a unit apart
		[Test]
		public void Simplify_MergesPointsLeftByBevelPlanes()
		{
			var hull = RegionPolygon.ConvexHull(new[]
			{
				new Point2(22088, -11200), new Point2(22088, -11240), new Point2(22087.98, -11240),
				new Point2(25200, -11096), new Point2(25200, -11056), new Point2(25200.01, -11056)
			});
			Assert.That(RegionPolygon.IsValid(hull), Is.False);

			var simplified = RegionPolygon.Simplify(hull);

			Assert.That(simplified, Is.Not.Null);
			Assert.That(RegionPolygon.IsValid(simplified!), Is.True);
			Assert.That(simplified!, Has.Count.EqualTo(4));
		}

		// kool_woodtory's checkpoint *46: a sheared strip whose bevels cut its corners by 2 units
		[Test]
		public void Simplify_RemovesShortEdges()
		{
			var polygon = new List<Point2>
			{
				new Point2(21352, -2634.03), new Point2(21352, -2632), new Point2(21392, -2632),
				new Point2(21392.53, -554.04), new Point2(21392.53, -552), new Point2(21352.53, -552)
			};

			var simplified = RegionPolygon.Simplify(RegionPolygon.ConvexHull(polygon));

			Assert.That(simplified, Is.Not.Null);
			Assert.That(RegionPolygon.IsValid(simplified!), Is.True);
			Assert.That(RegionPolygon.Area(simplified!), Is.EqualTo(RegionPolygon.Area(polygon)).Within(0.5).Percent);
		}

		[Test]
		public void Simplify_BluntsSharpCorners()
		{
			// A 10 degree wedge
			var tip = new Point2(0, 0);
			var simplified = RegionPolygon.Simplify(new List<Point2>
			{
				tip,
				new Point2(500, 0),
				new Point2(500 * Math.Cos(10 * Math.PI / 180), 500 * Math.Sin(10 * Math.PI / 180))
			});

			Assert.That(simplified, Is.Not.Null);
			Assert.That(RegionPolygon.IsValid(simplified!), Is.True);
			Assert.That(simplified!, Does.Not.Contain(tip));
		}

		[Test]
		public void Simplify_RejectsSlivers()
		{
			var simplified = RegionPolygon.Simplify(new List<Point2>
			{
				new Point2(0, 0), new Point2(100, 0), new Point2(100, 2), new Point2(0, 2)
			});

			Assert.That(simplified, Is.Null);
		}

		[Test]
		public void BoundingRectangle_WidensThinBounds()
		{
			var rectangle = RegionPolygon.BoundingRectangle(new[] { new Point2(0, 0), new Point2(100, 1) });

			Assert.That(RegionPolygon.IsValid(rectangle), Is.True);
		}

		[Test]
		public void Intersect_ReturnsOverlap()
		{
			var a = new List<Point2> { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10) };
			var b = new List<Point2> { new Point2(5, 5), new Point2(15, 5), new Point2(15, 15), new Point2(5, 15) };

			Assert.That(RegionPolygon.Area(RegionPolygon.Intersect(a, b)), Is.EqualTo(25).Within(1e-9));
		}
	}
}
