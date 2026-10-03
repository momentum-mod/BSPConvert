using System;
using System.Collections.Generic;
using System.Numerics;

namespace BSPConvert.Lib.GoldSrc
{
	// Finds the open space in a GoldSrc world that's under cover: where looking straight up, the first thing past any
	// empty space or liquid is solid rather than sky. Counter-Strike's client only spawns rain and snow where it would
	// see sky looking up from the drop (CEnvironment::UpdateRain traces up to z 8000), but Source's func_precipitation
	// only looks 512 units above the player, so it rains in caves and rooms taller than that. The space that's
	// covered higher up than that is given as boxes for func_precipitation_blockers, which stop drops whose column is
	// inside them at the player's height.
	public class GoldSrcSkyCover
	{
		private enum Cover : byte
		{
			// Open space under the sky, or solid with sky above it, where drops have to be allowed
			Exposed,
			// Open space under cover too high above for func_precipitation to find, where drops have to be stopped
			Covered,
			// Solid under cover, or open space that func_precipitation finds the cover of itself, where it doesn't matter
			Ignored,
		}

		// How far above the player func_precipitation looks for cover (CClient_Precipitation::ComputeEmissionArea)
		private const float PrecipitationTraceHeight = 512f;

		private readonly GoldSrcBsp gs;
		private readonly int headNode;
		private readonly List<(float top, float bottom, int contents)> column = new List<(float, float, int)>();

		public GoldSrcSkyCover(GoldSrcBsp gs)
		{
			this.gs = gs;
			headNode = gs.Models[0].headNodes[0];
		}

		// Covers the world's covered space with boxes, sampled on a grid of cells cellSize units wide. Boxes may overlap
		// and include solid space that's under cover.
		public List<(Vector3 mins, Vector3 maxs)> FindCoveredBoxes(float cellSize)
		{
			var world = gs.Models[0];
			var size = world.maxs - world.mins;
			var nx = Math.Max(1, (int)MathF.Ceiling(size.X / cellSize));
			var ny = Math.Max(1, (int)MathF.Ceiling(size.Y / cellSize));
			var nz = Math.Max(1, (int)MathF.Ceiling(size.Z / cellSize));

			var cells = new Cover[nx, ny, nz];
			for (var x = 0; x < nx; x++)
			{
				for (var y = 0; y < ny; y++)
				{
					var cx = world.mins.X + (x + 0.5f) * cellSize;
					var cy = world.mins.Y + (y + 0.5f) * cellSize;
					TraceColumn(cx, cy, world.maxs.Z + 1f, world.mins.Z - 1f);
					for (var z = 0; z < nz; z++)
						cells[x, y, z] = GetCover(world.mins.Z + (z + 0.5f) * cellSize, cellSize);
				}
			}

			var boxes = new List<(Vector3, Vector3)>();
			foreach (var (min, max) in MergeCells(cells, nx, ny, nz))
				boxes.Add((world.mins + new Vector3(min.x, min.y, min.z) * cellSize, Vector3.Min(world.maxs, world.mins + new Vector3(max.x, max.y, max.z) * cellSize)));

			return boxes;
		}

		// Whether the cell at a height in the traced column is under cover
		private Cover GetCover(float z, float cellSize)
		{
			var i = column.FindIndex(interval => z <= interval.top && z >= interval.bottom);
			if (i < 0)
				return Cover.Exposed;

			var startsSolid = IsSolid(column[i].contents);
			while (i >= 0 && IsSolid(column[i].contents))
				i--;
			while (i >= 0 && IsOpen(column[i].contents))
				i--;

			// Past the top of the world counts as sky
			if (i < 0 || column[i].contents == GoldSrcBsp.CONTENTS_SKY)
				return Cover.Exposed;

			// func_precipitation finds cover itself when it's close enough above
			if (startsSolid || column[i].bottom - (z - cellSize / 2f) < PrecipitationTraceHeight)
				return Cover.Ignored;

			return Cover.Covered;
		}

		private static bool IsOpen(int contents)
		{
			return contents is GoldSrcBsp.CONTENTS_EMPTY or GoldSrcBsp.CONTENTS_WATER or GoldSrcBsp.CONTENTS_SLIME or GoldSrcBsp.CONTENTS_LAVA;
		}

		private static bool IsSolid(int contents)
		{
			return !IsOpen(contents) && contents != GoldSrcBsp.CONTENTS_SKY;
		}

		// Lists the leaf contents along a vertical line from top to bottom, merging neighbors with the same contents
		private void TraceColumn(float x, float y, float top, float bottom)
		{
			column.Clear();
			TraceColumn(headNode, x, y, top, bottom);
		}

		private void TraceColumn(int nodeIndex, float x, float y, float top, float bottom)
		{
			while (nodeIndex >= 0)
			{
				var node = gs.Nodes[nodeIndex];
				var plane = gs.Planes[node.planeIndex];
				var offset = plane.normal.X * x + plane.normal.Y * y - plane.dist;
				var front = offset + plane.normal.Z * top;
				var back = offset + plane.normal.Z * bottom;
				if (front >= 0f && back >= 0f)
				{
					nodeIndex = node.child0;
					continue;
				}
				if (front < 0f && back < 0f)
				{
					nodeIndex = node.child1;
					continue;
				}

				var mid = top + (bottom - top) * (front / (front - back));
				TraceColumn(front >= 0f ? node.child0 : node.child1, x, y, top, mid);
				nodeIndex = front >= 0f ? node.child1 : node.child0;
				top = mid;
			}

			var contents = gs.Leaves[-nodeIndex - 1].contents;
			if (column.Count > 0 && column[^1].contents == contents)
				column[^1] = (column[^1].top, bottom, contents);
			else
				column.Add((top, bottom, contents));
		}

		// The orders a box can grow along the axes in
		private static readonly int[][] GrowthOrders = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };

		// Greedily covers the covered cells with boxes of cells that aren't exposed. Each box starts from the next
		// covered cell that no box has covered yet and grows along one axis after another, both ways, in whichever order
		// covers the most cells no box has yet. It's then shrunk to the covered cells in it. Returns cell ranges
		// [min, max).
		private static List<((int x, int y, int z) min, (int x, int y, int z) max)> MergeCells(Cover[,,] cells, int nx, int ny, int nz)
		{
			var size = new[] { nx, ny, nz };
			var done = new bool[nx, ny, nz];
			var boxes = new List<((int, int, int), (int, int, int))>();

			bool CanInclude(int[] lo, int[] hi)
			{
				for (var x = lo[0]; x < hi[0]; x++)
				{
					for (var y = lo[1]; y < hi[1]; y++)
					{
						for (var z = lo[2]; z < hi[2]; z++)
						{
							if (cells[x, y, z] == Cover.Exposed)
								return false;
						}
					}
				}

				return true;
			}

			// Adds another layer of cells to one side of the box along an axis if it can take it
			bool TryGrow(int[] lo, int[] hi, int axis, bool up)
			{
				var layer = up ? hi[axis] : lo[axis] - 1;
				if (layer < 0 || layer >= size[axis])
					return false;

				var layerLo = (int[])lo.Clone();
				var layerHi = (int[])hi.Clone();
				layerLo[axis] = layer;
				layerHi[axis] = layer + 1;
				if (!CanInclude(layerLo, layerHi))
					return false;

				if (up)
					hi[axis]++;
				else
					lo[axis]--;

				return true;
			}

			// Calls a function for each covered cell in a box
			void ForEachCovered(int[] lo, int[] hi, Action<int, int, int> action)
			{
				for (var x = lo[0]; x < hi[0]; x++)
				{
					for (var y = lo[1]; y < hi[1]; y++)
					{
						for (var z = lo[2]; z < hi[2]; z++)
						{
							if (cells[x, y, z] == Cover.Covered)
								action(x, y, z);
						}
					}
				}
			}

			for (var z = 0; z < nz; z++)
			{
				for (var y = 0; y < ny; y++)
				{
					for (var x = 0; x < nx; x++)
					{
						if (cells[x, y, z] != Cover.Covered || done[x, y, z])
							continue;

						int[]? bestLo = null;
						int[]? bestHi = null;
						var bestCount = -1;
						foreach (var order in GrowthOrders)
						{
							var lo = new[] { x, y, z };
							var hi = new[] { x + 1, y + 1, z + 1 };
							foreach (var axis in order)
							{
								while (TryGrow(lo, hi, axis, true)) { }
								while (TryGrow(lo, hi, axis, false)) { }
							}

							var count = 0;
							ForEachCovered(lo, hi, (cx, cy, cz) => count += done[cx, cy, cz] ? 0 : 1);
							if (count > bestCount)
								(bestLo, bestHi, bestCount) = (lo, hi, count);
						}

						// Shrink to the covered cells, and mark them done
						var min = (x: nx, y: ny, z: nz);
						var max = (x: 0, y: 0, z: 0);
						ForEachCovered(bestLo!, bestHi!, (cx, cy, cz) =>
						{
							done[cx, cy, cz] = true;
							min = (Math.Min(min.x, cx), Math.Min(min.y, cy), Math.Min(min.z, cz));
							max = (Math.Max(max.x, cx + 1), Math.Max(max.y, cy + 1), Math.Max(max.z, cz + 1));
						});

						boxes.Add((min, max));
					}
				}
			}

			return boxes;
		}
	}
}
