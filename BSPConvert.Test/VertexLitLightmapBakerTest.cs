using BSPConvert.Lib;
using System.Numerics;

namespace BSPConvert.Test
{
	public class VertexLitLightmapBakerTests
	{
		private static readonly Q3Brightness brightness = new Q3Brightness();

		private static Vector3 ToLinear(Vector3 color) =>
			new Vector3(brightness.LightToLinear(color.X), brightness.LightToLinear(color.Y), brightness.LightToLinear(color.Z));

		// Bilinear filtering at a coord in luxels (luxel x's center is at x + 0.5), clamped to the edge like the guard band
		private static Vector3 Sample(VertexLitLightmap lightmap, Vector2 coord)
		{
			var x = coord.X - 0.5f;
			var y = coord.Y - 0.5f;
			var x0 = (int)MathF.Floor(x);
			var y0 = (int)MathF.Floor(y);
			Vector3 Luxel(int lx, int ly) => lightmap.Luxels[Math.Clamp(lx, 0, lightmap.Width - 1) + Math.Clamp(ly, 0, lightmap.Height - 1) * lightmap.Width];

			return Vector3.Lerp(
				Vector3.Lerp(Luxel(x0, y0), Luxel(x0 + 1, y0), x - x0),
				Vector3.Lerp(Luxel(x0, y0 + 1), Luxel(x0 + 1, y0 + 1), x - x0),
				y - y0);
		}

		[Test]
		public void TrianglesMatchTheirVertexColorsAndAreLinearBetweenThem()
		{
			var colors = new[] { new Vector3(40, 50, 60), new Vector3(60, 50, 40), new Vector3(50, 70, 50), new Vector3(45, 45, 45) };
			var indices = new[] { 0, 1, 2, 2, 1, 3 };
			var lightmap = VertexLitLightmapBaker.BakeTriangles(colors, indices, ToLinear);

			for (var i = 0; i < indices.Length; i++)
				Assert.That(Vector3.Distance(Sample(lightmap, lightmap.Coords[i]), ToLinear(colors[indices[i]])), Is.LessThan(1e-5f));

			for (var t = 0; t < indices.Length; t += 3)
			{
				var centroid = (lightmap.Coords[t] + lightmap.Coords[t + 1] + lightmap.Coords[t + 2]) / 3f;
				var corners = (ToLinear(colors[indices[t]]) + ToLinear(colors[indices[t + 1]]) + ToLinear(colors[indices[t + 2]])) / 3f;
				Assert.That(Vector3.Distance(Sample(lightmap, centroid), corners), Is.LessThan(1e-5f));
			}
		}

		[Test]
		public void ContrastyTrianglesFollowGammaSpaceShading()
		{
			var colors = new[] { new Vector3(10), new Vector3(200), new Vector3(10) };
			var lightmap = VertexLitLightmapBaker.BakeTriangles(colors, new[] { 0, 1, 2 }, ToLinear);

			// Quake 3 shades the middle of the edge at the average color, 105, which linear filtering would brighten to ~146
			var middle = Sample(lightmap, (lightmap.Coords[0] + lightmap.Coords[1]) / 2f).X;
			Assert.That(middle, Is.EqualTo(brightness.LightToLinear(105)).Within(10).Percent);
		}

		[Test]
		public void FlatColorsTakeOneLuxel()
		{
			var colors = new[] { new Vector3(30), new Vector3(31), new Vector3(32) };
			var lightmap = VertexLitLightmapBaker.BakeTriangles(colors, new[] { 0, 1, 2 }, ToLinear);

			Assert.That((lightmap.Width, lightmap.Height), Is.EqualTo((1, 1)));
			Assert.That(lightmap.Coords, Is.All.EqualTo(new Vector2(0.5f, 0.5f)));
		}

		[Test]
		public void PatchesMatchTheirCornersAndCurveBetweenControlPoints()
		{
			// A flat 5x3 patch (two sub-patches) 16 units across, lit brightest at its middle control column
			var positions = new Vector3[15];
			var colors = new Vector3[15];
			for (var j = 0; j < 3; j++)
			{
				for (var i = 0; i < 5; i++)
				{
					positions[i + j * 5] = new Vector3(i * 4, j * 4, 0);
					colors[i + j * 5] = new Vector3(i == 2 ? 200 : 20);
				}
			}

			var lightmap = VertexLitLightmapBaker.BakePatch(positions, colors, 5, 3, 16f, ToLinear);
			Assert.That(lightmap.Width, Is.GreaterThanOrEqualTo(9), "4 luxels per sub-patch, however small");

			foreach (var corner in new[] { 0, 2, 4, 10, 12, 14 })
				Assert.That(Vector3.Distance(Sample(lightmap, lightmap.Coords[corner]), ToLinear(colors[corner])), Is.LessThan(1e-4f));

			// Halfway along the first sub-patch the bezier color is (20 + 2 * 20 + 200) / 4 = 65
			var quarter = Sample(lightmap, (lightmap.Coords[0] + lightmap.Coords[2]) / 2f).X;
			Assert.That(quarter, Is.EqualTo(brightness.LightToLinear(65)).Within(2).Percent);
		}
	}
}
