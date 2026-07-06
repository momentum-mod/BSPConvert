using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;

using Vector3 = System.Numerics.Vector3;
using Color = System.Drawing.Color;

namespace BSPConvert.Lib
{
	public class LightGridConverter
	{
		private const int MAX_SAMPLES_PER_LEAF = 16;
		private const int MAX_SAMPLES_PER_AXIS = 4;

		// see: tr_light.c, tr_init.c
		private const float AMBIENT_SCALE = 0.6f;
		private const float DIRECTED_SCALE = 1f;

		private static readonly Vector3[] boxDirections =
		{
			Vector3.UnitX, -Vector3.UnitX,
			Vector3.UnitY, -Vector3.UnitY,
			Vector3.UnitZ, -Vector3.UnitZ
		};

		private BSP quakeBsp;
		private BSP sourceBsp;
		private bool clampOverbright;

		private Lump<LightGridPoint> gridPoints;
		private Vector3 gridOrigin = Vector3.Zero;
		private Vector3 gridSize;
		private int[] gridBounds = new int[3];
		private int[] gridStep = new int[3];

		public LightGridConverter(BSP quakeBsp, BSP sourceBsp, bool clampOverbright)
		{
			this.quakeBsp = quakeBsp;
			this.sourceBsp = sourceBsp;
			this.clampOverbright = clampOverbright;
		}

		public void Convert()
		{
			if (!LoadLightGrid())
				return;

			// TODO: support MapType.Source20
			SetLumpVersionNumber(LeafAmbientLighting.GetIndexForLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(LeafAmbientLighting.GetIndexForHDRLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(LeafAmbientIndex.GetIndexForLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(LeafAmbientIndex.GetIndexForHDRLump(sourceBsp.MapType), 1);

			// TODO: handle too many leaf samples
			var leafSamples = BuildLeafSamples();

			if (!leafSamples.Any(x => x.Count > 0))
				return;

			var nearestLitLeaves = FindNearestLitLeaves(leafSamples);
			WriteAmbientLumps(leafSamples, nearestLitLeaves);
		}

		// see: R_LoadLightGrid
		private bool LoadLightGrid()
		{
			gridPoints = quakeBsp.LightGrid;
			if (gridPoints == null || gridPoints.Count == 0)
				return false;

			gridSize = GetGridSize();

			var worldModel = quakeBsp.Models[0];
			var mins = worldModel.Minimums;
			var maxs = worldModel.Maximums;

			var numGridPoints = 1L;
			for (var i = 0; i < 3; i++)
			{
				var origin = gridSize[i] * MathF.Ceiling(mins[i] / gridSize[i]);
				var max = gridSize[i] * MathF.Floor(maxs[i] / gridSize[i]);

				gridOrigin[i] = origin;
				gridBounds[i] = (int)MathF.Round((max - origin) / gridSize[i]) + 1;
				if (gridBounds[i] < 1)
					gridBounds[i] = 1;

				numGridPoints *= gridBounds[i];
			}

			if (gridPoints.Count != numGridPoints)
				return false;

			gridStep[0] = 1;
			gridStep[1] = gridBounds[0];
			gridStep[2] = gridBounds[0] * gridBounds[1];

			if (clampOverbright)
				ClampGridOverbright();

			return true;
		}

		private Vector3 GetGridSize()
		{
			var defaultGridSize = new Vector3(64f, 64f, 128f);

			var worldspawn = quakeBsp.Entities.FirstOrDefault(x => x.ClassName == "worldspawn");
			if (worldspawn == null || !worldspawn.TryGetValue("gridsize", out var value))
				return defaultGridSize;

			var components = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (components.Length < 3)
				return defaultGridSize;

			var gridSize = new Vector3();
			for (var i = 0; i < 3; i++)
			{
				if (!float.TryParse(components[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0f)
					return defaultGridSize;

				gridSize[i] = size;
			}

			return gridSize;
		}

		private void ClampGridOverbright()
		{
			for (var i = 0; i < gridPoints.Count; i++)
			{
				var point = gridPoints[i];

				var ambient = point.Ambient;
				var (ar, ag, ab) = ColorUtil.ApplyOverbrightClamp(ambient.R, ambient.G, ambient.B);
				point.Ambient = Color.FromArgb(ar, ag, ab);

				var directed = point.Directed;
				var (dr, dg, db) = ColorUtil.ApplyOverbrightClamp(directed.R, directed.G, directed.B);
				point.Directed = Color.FromArgb(dr, dg, db);
			}
		}

		private List<List<LeafAmbientLighting>> BuildLeafSamples()
		{
			var lightingLump = sourceBsp.LeafAmbientLighting;
			var leafSamples = new List<List<LeafAmbientLighting>>(sourceBsp.Leaves.Count);

			for (var i = 0; i < sourceBsp.Leaves.Count; i++)
			{
				var leaf = sourceBsp.Leaves[i];
				var samples = new List<LeafAmbientLighting>();
				leafSamples.Add(samples);

				var mins = leaf.Minimums;
				var maxs = leaf.Maximums;

				if (i == 0 || mins.X > maxs.X || mins.Y > maxs.Y || mins.Z > maxs.Z)
					continue;

				var size = maxs - mins;
				var isTreeLeaf = i < quakeBsp.Leaves.Count;

				var sampleCounts = GetSampleCounts(size);
				foreach (var x in GetAxisFractions(sampleCounts[0]))
				{
					foreach (var y in GetAxisFractions(sampleCounts[1]))
					{
						foreach (var z in GetAxisFractions(sampleCounts[2]))
						{
							var position = mins + size * new Vector3(x, y, z);

							// Reject positions that fall into a neighboring leaf
							if (isTreeLeaf && FindQ3LeafIndex(position) != i)
								continue;

							if (!SampleGrid(position, out var ambient, out var directed, out var lightDir))
								continue;

							var data = new byte[LeafAmbientLighting.GetStructLength(sourceBsp.MapType, lightingLump.LumpInfo.version)];
							var sample = new LeafAmbientLighting(data, lightingLump)
							{
								X = (byte)(x * 255f + 0.5f),
								Y = (byte)(y * 255f + 0.5f),
								Z = (byte)(z * 255f + 0.5f)
							};
							SetAmbientCube(sample, ambient, directed, lightDir);
							samples.Add(sample);
						}
					}
				}
			}

			return leafSamples;
		}

		private int[] GetSampleCounts(Vector3 leafSize)
		{
			var counts = new int[3];
			for (var i = 0; i < 3; i++)
				counts[i] = Math.Clamp((int)MathF.Ceiling(leafSize[i] / (gridSize[i] * 1.5f)), 1, MAX_SAMPLES_PER_AXIS);

			while (counts[0] * counts[1] * counts[2] > MAX_SAMPLES_PER_LEAF)
			{
				var largest = Array.IndexOf(counts, counts.Max());
				counts[largest]--;
			}

			return counts;
		}

		private static float[] GetAxisFractions(int count)
		{
			var fractions = new float[count];
			for (var i = 0; i < count; i++)
				fractions[i] = (i + 0.5f) / count;

			return fractions;
		}

		private int FindQ3LeafIndex(Vector3 position)
		{
			var nodeIndex = 0;
			while (nodeIndex >= 0)
			{
				var node = quakeBsp.Nodes[nodeIndex];
				var plane = quakeBsp.Planes[node.PlaneIndex];

				nodeIndex = Vector3.Dot(plane.Normal, position) - plane.Distance >= 0f ? node.Child1Index : node.Child2Index;
			}

			return -(nodeIndex + 1);
		}

		// see: R_SetupEntityLightingGrid
		private bool SampleGrid(Vector3 position, out float[] ambient, out float[] directed, out Vector3 lightDir)
		{
			ambient = new float[3];
			directed = new float[3];
			lightDir = Vector3.Zero;

			var cell = new int[3];
			var frac = new float[3];
			for (var i = 0; i < 3; i++)
			{
				var v = (position[i] - gridOrigin[i]) / gridSize[i];

				cell[i] = (int)MathF.Floor(v);
				frac[i] = v - cell[i];
				if (cell[i] < 0)
				{
					cell[i] = 0;
					frac[i] = 0f;
				}
				else if (cell[i] > gridBounds[i] - 1)
				{
					cell[i] = gridBounds[i] - 1;
					frac[i] = 0f;
				}
			}

			var startOffset = cell[0] * gridStep[0] + cell[1] * gridStep[1] + cell[2] * gridStep[2];

			var totalFactor = 0f;
			for (var i = 0; i < 8; i++)
			{
				var factor = 1f;
				var offset = startOffset;

				var outsideGrid = false;
				for (var j = 0; j < 3; j++)
				{
					if ((i & (1 << j)) != 0)
					{
						if (cell[j] + 1 > gridBounds[j] - 1)
						{
							outsideGrid = true;
							break;
						}

						factor *= frac[j];
						offset += gridStep[j];
					}
					else
						factor *= 1f - frac[j];
				}

				if (outsideGrid)
					continue;

				var point = gridPoints[offset];
				var pointAmbient = point.Ambient;
				if (pointAmbient.R + pointAmbient.G + pointAmbient.B == 0)
					continue;

				totalFactor += factor;

				ambient[0] += factor * pointAmbient.R;
				ambient[1] += factor * pointAmbient.G;
				ambient[2] += factor * pointAmbient.B;

				var pointDirected = point.Directed;
				directed[0] += factor * pointDirected.R;
				directed[1] += factor * pointDirected.G;
				directed[2] += factor * pointDirected.B;

				lightDir += factor * DecodeLatLong(point.Longitude, point.Latitude);
			}

			if (totalFactor <= 0f)
				return false;

			if (totalFactor < 0.99f)
			{
				var scale = 1f / totalFactor;
				for (var i = 0; i < 3; i++)
				{
					ambient[i] *= scale;
					directed[i] *= scale;
				}

				lightDir *= scale;
			}

			if (lightDir.LengthSquared() > 0f)
				lightDir = Vector3.Normalize(lightDir);

			return true;
		}

		private static Vector3 DecodeLatLong(byte lngByte, byte latByte)
		{
			var lat = latByte * (MathF.PI * 2f / 256f);
			var lng = lngByte * (MathF.PI * 2f / 256f);

			return new Vector3(
				MathF.Cos(lat) * MathF.Sin(lng),
				MathF.Sin(lat) * MathF.Sin(lng),
				MathF.Cos(lng));
		}

		private void SetAmbientCube(LeafAmbientLighting sample, float[] ambient, float[] directed, Vector3 lightDir)
		{
			for (var i = 0; i < LeafAmbientLighting.NumCubeSides; i++)
			{
				var dot = MathF.Max(0f, Vector3.Dot(lightDir, boxDirections[i]));

				var r = ambient[0] * AMBIENT_SCALE + directed[0] * DIRECTED_SCALE * dot;
				var g = ambient[1] * AMBIENT_SCALE + directed[1] * DIRECTED_SCALE * dot;
				var b = ambient[2] * AMBIENT_SCALE + directed[2] * DIRECTED_SCALE * dot;

				var color = ColorUtil.ConvertQ3LightGridColorToColorRGBExp32(r, g, b, clampOverbright);
				sample.SetColor(i, Color.FromArgb(color.r, color.g, color.b));
				sample.SetExponent(i, color.exponent);
			}
		}

		private int[] FindNearestLitLeaves(List<List<LeafAmbientLighting>> leafSamples)
		{
			var litLeaves = new List<(int index, Vector3 center)>();
			for (var i = 1; i < leafSamples.Count; i++)
			{
				if (leafSamples[i].Count > 0)
				{
					var leaf = sourceBsp.Leaves[i];
					litLeaves.Add((i, (leaf.Minimums + leaf.Maximums) * 0.5f));
				}
			}

			var nearestLitLeaves = new int[leafSamples.Count];
			for (var i = 1; i < leafSamples.Count; i++)
			{
				if (leafSamples[i].Count > 0 || litLeaves.Count == 0)
					continue;

				var leaf = sourceBsp.Leaves[i];
				var center = (leaf.Minimums + leaf.Maximums) * 0.5f;

				var bestDistSq = float.MaxValue;
				foreach (var (index, litCenter) in litLeaves)
				{
					var distSq = (litCenter - center).LengthSquared();
					if (distSq < bestDistSq)
					{
						bestDistSq = distSq;
						nearestLitLeaves[i] = index;
					}
				}
			}

			return nearestLitLeaves;
		}

		private void WriteAmbientLumps(List<List<LeafAmbientLighting>> leafSamples, int[] nearestLitLeaves)
		{
			var lighting = sourceBsp.LeafAmbientLighting;
			var lightingHDR = sourceBsp.LeafAmbientLightingHDR;
			var indices = sourceBsp.LeafAmbientIndices;
			var indicesHDR = sourceBsp.LeafAmbientIndicesHDR;

			var sampleOffset = 0;
			for (var i = 0; i < leafSamples.Count; i++)
			{
				var samples = leafSamples[i];

				int count;
				int firstSample;
				if (samples.Count > 0)
				{
					count = samples.Count;
					firstSample = sampleOffset;
					sampleOffset += count;

					foreach (var sample in samples)
					{
						lighting.Add(sample);
						lightingHDR.Add(new LeafAmbientLighting(sample, lightingHDR));
					}
				}
				else
				{
					count = 0;
					firstSample = nearestLitLeaves[i];
				}

				var data = new byte[LeafAmbientIndex.GetStructLength(sourceBsp.MapType, indices.LumpInfo.version)];
				var index = new LeafAmbientIndex(data, indices)
				{
					AmbientSampleCount = (uint)count,
					FirstAmbientSample = (uint)firstSample
				};
				indices.Add(index);
				indicesHDR.Add(new LeafAmbientIndex(index, indicesHDR));
			}
		}

		private void SetLumpVersionNumber(int lumpIndex, int lumpVersion)
		{
			var lumpInfo = sourceBsp[lumpIndex];
			lumpInfo.version = lumpVersion;
			sourceBsp[lumpIndex] = lumpInfo;
		}
	}
}
