using System.Numerics;

namespace BSPConvert.Lib
{
	// A lightmap baked from a surface's vertex colors
	public class VertexLitLightmap
	{
		public int Width;
		public int Height;
		// Linear light, row by row
		public Vector3[] Luxels = Array.Empty<Vector3>();
		// Where each patch vertex, or each other face's triangle corner, samples the lightmap, in luxels (luxel x's
		// center is at x + 0.5)
		public Vector2[] Coords = Array.Empty<Vector2>();
	}

	// Bakes the vertex colors q3map2 lights some surfaces with into lightmaps, interpolated the way Quake 3 draws them:
	// across each triangle, and along a patch's curves. Colors are 0-255; toLinear gives the linear light they're drawn
	// with.
	public static class VertexLitLightmapBaker
	{
		// Colors this close on every channel are drawn as one luxel
		private const float FlatTolerance = 2f;
		// Fewest luxels along each 3x3 sub-patch, which can light its control points very differently however small it is
		private const int MinSubPatchLuxels = 4;
		private const int MaxPatchLuxels = 128;
		private const float GammaStep = 32f;
		private const int MaxTriangleCellLuxels = 8;

		// Gives each triangle a square cell of luxels, its corners on three of the cell's corner luxels' centers. Luxels past
		// its far edge complete the parallelograms of the luxels along it, which makes bilinear filtering linear along
		// every edge, so neighbouring triangles meet without seams.
		public static VertexLitLightmap BakeTriangles(Vector3[] colors, int[] indices, Func<Vector3, Vector3> toLinear)
		{
			var triangles = indices.Length / 3;
			if (triangles == 0 || IsFlat(colors))
				return BakeFlat(colors, indices.Length, toLinear);

			var cell = GetTriangleCellLuxels(colors, indices);
			var last = cell - 1;
			var columns = (int)Math.Ceiling(Math.Sqrt(triangles));
			var rows = (triangles + columns - 1) / columns;
			var lightmap = new VertexLitLightmap
			{
				Width = cell * columns,
				Height = cell * rows,
				Coords = new Vector2[indices.Length]
			};
			lightmap.Luxels = new Vector3[lightmap.Width * lightmap.Height];

			for (var t = 0; t < triangles; t++)
			{
				var x = cell * (t % columns);
				var y = cell * (t / columns);
				var corner = GetRightAngleCorner(t, colors, indices, toLinear);
				var ka = 3 * t + corner;
				var kb = 3 * t + (corner + 1) % 3;
				var kc = 3 * t + (corner + 2) % 3;
				var a = colors[indices[ka]];
				var b = colors[indices[kb]];
				var c = colors[indices[kc]];
				int Luxel(int i, int j) => x + i + (y + j) * lightmap.Width;

				for (var j = 0; j <= last; j++)
				{
					for (var i = 0; i + j <= last; i++)
						lightmap.Luxels[Luxel(i, j)] = toLinear(a + (b - a) * (i / (float)last) + (c - a) * (j / (float)last));
				}

				for (var j = 0; j < last; j++)
				{
					var i = last - 1 - j;
					var far = lightmap.Luxels[Luxel(i + 1, j)] + lightmap.Luxels[Luxel(i, j + 1)] - lightmap.Luxels[Luxel(i, j)];
					lightmap.Luxels[Luxel(i + 1, j + 1)] = Vector3.Max(far, Vector3.Zero);
				}

				lightmap.Coords[ka] = new Vector2(x + 0.5f, y + 0.5f);
				lightmap.Coords[kb] = new Vector2(x + last + 0.5f, y + 0.5f);
				lightmap.Coords[kc] = new Vector2(x + 0.5f, y + last + 0.5f);
			}

			return lightmap;
		}

		// The triangle's corner (0-2) to put at its cell's right angle: the one that keeps the luxels completing the cell,
		// b + c - a, furthest from negative light, which luxels can't store
		private static int GetRightAngleCorner(int triangle, Vector3[] colors, int[] indices, Func<Vector3, Vector3> toLinear)
		{
			var corners = new[] { toLinear(colors[indices[3 * triangle]]), toLinear(colors[indices[3 * triangle + 1]]), toLinear(colors[indices[3 * triangle + 2]]) };
			var best = 0;
			var bestLeast = float.MinValue;
			for (var corner = 0; corner < 3; corner++)
			{
				var far = corners[(corner + 1) % 3] + corners[(corner + 2) % 3] - corners[corner];
				var least = Math.Min(far.X, Math.Min(far.Y, far.Z));
				if (least > bestLeast)
				{
					bestLeast = least;
					best = corner;
				}
			}

			return best;
		}

		// Lightmaps are filtered in linear light, but Quake 3 shades between vertex colors in gamma space. A luxel every
		// GammaStep of a triangle's color range keeps the colors between them within a few levels of Quake 3's.
		private static int GetTriangleCellLuxels(Vector3[] colors, int[] indices)
		{
			var range = 0f;
			for (var i = 0; i + 2 < indices.Length; i += 3)
			{
				var a = colors[indices[i]];
				var b = colors[indices[i + 1]];
				var c = colors[indices[i + 2]];
				var spread = Vector3.Max(a, Vector3.Max(b, c)) - Vector3.Min(a, Vector3.Min(b, c));
				range = Math.Max(range, Math.Max(spread.X, Math.Max(spread.Y, spread.Z)));
			}

			return Math.Clamp((int)Math.Ceiling(range / GammaStep) + 1, 2, MaxTriangleCellLuxels);
		}

		// A luxel grid across the patch, at most luxelSize units apart along its control points. positions and colors are
		// its control points', row by row.
		public static VertexLitLightmap BakePatch(Vector3[] positions, Vector3[] colors, int width, int height, float luxelSize, Func<Vector3, Vector3> toLinear)
		{
			if (width < 3 || height < 3 || width % 2 == 0 || height % 2 == 0 || colors.Length != width * height || IsFlat(colors))
				return BakeFlat(colors, colors.Length, toLinear);

			var lightmap = new VertexLitLightmap
			{
				Width = GetPatchLuxels(positions, width, height, 1, width, luxelSize),
				Height = GetPatchLuxels(positions, height, width, width, 1, luxelSize),
				Coords = new Vector2[colors.Length]
			};
			lightmap.Luxels = new Vector3[lightmap.Width * lightmap.Height];

			for (var y = 0; y < lightmap.Height; y++)
			{
				for (var x = 0; x < lightmap.Width; x++)
				{
					var u = x / (lightmap.Width - 1f);
					var v = y / (lightmap.Height - 1f);
					lightmap.Luxels[x + y * lightmap.Width] = toLinear(EvaluatePatch(colors, width, height, u, v));
				}
			}

			for (var j = 0; j < height; j++)
			{
				for (var i = 0; i < width; i++)
				{
					lightmap.Coords[i + j * width] = new Vector2(
						0.5f + i / (width - 1f) * (lightmap.Width - 1),
						0.5f + j / (height - 1f) * (lightmap.Height - 1));
				}
			}

			return lightmap;
		}

		private static VertexLitLightmap BakeFlat(Vector3[] colors, int coordCount, Func<Vector3, Vector3> toLinear)
		{
			var average = colors.Length > 0 ? colors.Aggregate(Vector3.Zero, (sum, color) => sum + color) / colors.Length : Vector3.Zero;
			var coords = new Vector2[coordCount];
			Array.Fill(coords, new Vector2(0.5f, 0.5f));

			return new VertexLitLightmap
			{
				Width = 1,
				Height = 1,
				Luxels = new[] { toLinear(average) },
				Coords = coords
			};
		}

		private static bool IsFlat(Vector3[] colors)
		{
			if (colors.Length == 0)
				return true;

			var min = colors.Aggregate(Vector3.Min);
			var max = colors.Aggregate(Vector3.Max);
			var range = max - min;
			return Math.Max(range.X, Math.Max(range.Y, range.Z)) <= FlatTolerance;
		}

		// Luxels along the patch's lines of control points that run 'step' apart, 'stride' apart from each other (rows:
		// step 1, stride width), enough for its longest one
		private static int GetPatchLuxels(Vector3[] positions, int pointsPerLine, int lines, int step, int stride, float luxelSize)
		{
			var longest = 0f;
			for (var line = 0; line < lines; line++)
			{
				var length = 0f;
				for (var i = 1; i < pointsPerLine; i++)
					length += Vector3.Distance(positions[line * stride + i * step], positions[line * stride + (i - 1) * step]);

				longest = Math.Max(longest, length);
			}

			var luxels = Math.Max((int)Math.Ceiling(longest / luxelSize), (pointsPerLine - 1) / 2 * MinSubPatchLuxels) + 1;
			return Math.Min(luxels, MaxPatchLuxels);
		}

		// The patch's value at u, v (0-1 across all of it), on the 3x3 sub-patch there
		private static Vector3 EvaluatePatch(Vector3[] points, int width, int height, float u, float v)
		{
			var subPatchesU = (width - 1) / 2;
			var subPatchesV = (height - 1) / 2;
			var su = u * subPatchesU;
			var sv = v * subPatchesV;
			var sx = Math.Min((int)su, subPatchesU - 1);
			var sy = Math.Min((int)sv, subPatchesV - 1);

			var controlPoints = new Vector3[9];
			for (var j = 0; j < 3; j++)
			{
				for (var i = 0; i < 3; i++)
					controlPoints[i + j * 3] = points[2 * sx + i + (2 * sy + j) * width];
			}

			return new BezierPatch(controlPoints).GetPoint(su - sx, sv - sy);
		}
	}
}
