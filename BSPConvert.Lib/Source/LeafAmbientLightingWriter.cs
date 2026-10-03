using LibBSP;
using System.Collections.Generic;

using Vector3 = System.Numerics.Vector3;

namespace BSPConvert.Lib
{
	// Writes the leaves' ambient lighting samples (the ambient cubes models are lit with) to the LDR and HDR lumps
	public static class LeafAmbientLightingWriter
	{
		// Creates an empty sample at a position in its leaf, given as fractions of the leaf's size
		public static LeafAmbientLighting CreateSample(BSP sourceBsp, float x, float y, float z)
		{
			var lightingLump = sourceBsp.LeafAmbientLighting;
			var data = new byte[LeafAmbientLighting.GetStructLength(sourceBsp.MapType, lightingLump.LumpInfo.version)];
			return new LeafAmbientLighting(data, lightingLump)
			{
				X = (byte)(x * 255f + 0.5f),
				Y = (byte)(y * 255f + 0.5f),
				Z = (byte)(z * 255f + 0.5f)
			};
		}

		// Sets the lump versions the samples are created with. Call before CreateSample.
		public static void SetLumpVersions(BSP sourceBsp)
		{
			// TODO: support MapType.Source20
			SetLumpVersionNumber(sourceBsp, LeafAmbientLighting.GetIndexForLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(sourceBsp, LeafAmbientLighting.GetIndexForHDRLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(sourceBsp, LeafAmbientIndex.GetIndexForLump(sourceBsp.MapType), 1);
			SetLumpVersionNumber(sourceBsp, LeafAmbientIndex.GetIndexForHDRLump(sourceBsp.MapType), 1);
		}

		// Writes each leaf's samples (one list per leaf). Leaves without samples use the nearest leaf that has them.
		public static void Write(BSP sourceBsp, List<List<LeafAmbientLighting>> leafSamples)
		{
			var nearestLitLeaves = FindNearestLitLeaves(sourceBsp, leafSamples);

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
					// With no samples, the engine takes this as the leaf whose samples to use
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

		private static int[] FindNearestLitLeaves(BSP sourceBsp, List<List<LeafAmbientLighting>> leafSamples)
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

		private static void SetLumpVersionNumber(BSP sourceBsp, int lumpIndex, int lumpVersion)
		{
			var lumpInfo = sourceBsp[lumpIndex];
			lumpInfo.version = lumpVersion;
			sourceBsp[lumpIndex] = lumpInfo;
		}
	}
}
