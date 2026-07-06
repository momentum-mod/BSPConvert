using LibBSP;
using System;
using System.Collections.Generic;
using System.Linq;

using Vector3 = System.Numerics.Vector3;

namespace BSPConvert.Lib
{
	// One skybox region: a distinct sky's visibility volume, expressed as a set of axis-aligned boxes
	// (merged leaf bounds) that a trigger_multiple covers to swap sv_skyname to SkyName on entry.
	public class SkyboxSwapRegion
	{
		public required string SkyName;
		public List<(Vector3 mins, Vector3 maxs)> Boxes = new List<(Vector3, Vector3)>();
	}

	// The result of skybox-swap detection: which sky the map spawns with, plus the per-sky trigger
	// regions. Regions is empty when the map has 0-1 skyboxes, has no vis data, or has co-visible
	// skyboxes - in all of those cases the converter keeps its single-skybox behaviour (DefaultSkyName).
	public class SkyboxSwapPlan
	{
		public string? DefaultSkyName;
		public List<SkyboxSwapRegion> Regions = new List<SkyboxSwapRegion>();
	}

	// Detects Quake 3 maps that use more than one skybox and works out how to reproduce them with Strata's
	// skybox_swapper entity. Source only has one global 2D skybox (sv_skyname); the swapper changes it at
	// runtime. That can only faithfully represent a Q3 map whose skyboxes are never on screen at the same
	// time, so this uses the BSP's PVS to (a) reject maps where two skyboxes are co-visible and (b) build,
	// for each skybox, the set of leaves from which it can be seen - the volume whose trigger swaps to it.
	//
	// See BSPConverter.ConvertSkyboxSwappers for the entity/brush emission that consumes the plan.
	public class SkyboxSwapConverter
	{
		private readonly BSP bsp;
		private readonly Func<Texture, string?> resolveSkyboxName; // Source skyname for a sky texture, else null
		private readonly ILogger logger;
		private readonly string? fallbackSkyName;

		public SkyboxSwapConverter(BSP bsp, Func<Texture, string?> resolveSkyboxName, ILogger logger, string? fallbackSkyName)
		{
			this.bsp = bsp;
			this.resolveSkyboxName = resolveSkyboxName;
			this.logger = logger;
			this.fallbackSkyName = fallbackSkyName;
		}

		public SkyboxSwapPlan Build()
		{
			var plan = new SkyboxSwapPlan { DefaultSkyName = fallbackSkyName };

			var vis = bsp.Visibility;
			if (vis?.Data == null || vis.Data.Length < 8)
				return plan; // unvised map - can't reason about co-visibility, keep single skybox

			var numClusters = vis.NumClusters;
			var clusterBytes = vis.ClusterSize;
			if (numClusters <= 0 || clusterBytes <= 0)
				return plan;

			// The PVS bit vectors start after the two header ints; cluster A sees cluster B when bit B is set
			// in A's vector. Q3 stores this uncompressed (unlike Quake 1/2), so it's a direct bit test.
			bool CanSee(int from, int to)
			{
				if (from < 0 || to < 0)
					return false;
				var byteIndex = 8 + from * clusterBytes + (to >> 3);
				if (byteIndex < 0 || byteIndex >= vis.Data.Length)
					return false;
				return (vis.Data[byteIndex] & (1 << (to & 7))) != 0;
			}

			// Clusters that contain a sky face, grouped by that sky's Source skyname.
			var clustersBySky = new Dictionary<string, HashSet<int>>();
			foreach (var leaf in bsp.Leaves)
			{
				var cluster = leaf.Visibility;
				if (cluster < 0 || cluster >= numClusters)
					continue;

				foreach (var faceIndex in leaf.MarkFaces)
				{
					if (faceIndex < 0 || faceIndex >= bsp.Faces.Count)
						continue;

					var skyName = resolveSkyboxName(bsp.Faces[faceIndex].Texture);
					if (string.IsNullOrEmpty(skyName))
						continue;

					if (!clustersBySky.TryGetValue(skyName, out var set))
						clustersBySky[skyName] = set = new HashSet<int>();
					set.Add(cluster);
				}
			}

			if (clustersBySky.Count <= 1)
				return plan; // 0-1 skyboxes: nothing to swap between

			// For every open cluster, find which single skybox it can see. If any cluster can see two
			// different skyboxes they'd render simultaneously in Q3, which the swapper can't reproduce -
			// bail out and keep the single-skybox path rather than popping the whole sky at a boundary.
			var skyNames = clustersBySky.Keys.ToList();
			var skyByCluster = new Dictionary<int, string>();
			foreach (var leaf in bsp.Leaves)
			{
				var cluster = leaf.Visibility;
				if (cluster < 0 || cluster >= numClusters || skyByCluster.ContainsKey(cluster))
					continue;

				string? seen = null;
				foreach (var skyName in skyNames)
				{
					if (!clustersBySky[skyName].Any(skyCluster => CanSee(cluster, skyCluster)))
						continue;

					if (seen != null && seen != skyName)
					{
						logger.Log($"Skybox swap: skyboxes '{seen}' and '{skyName}' are co-visible; keeping a single skybox.");
						return plan;
					}
					seen = skyName;
				}

				if (seen != null)
					skyByCluster[cluster] = seen;
			}

			// Build each skybox's trigger volume from the bounds of every leaf whose cluster sees it.
			var boxesBySky = new Dictionary<string, List<(Vector3, Vector3)>>();
			foreach (var leaf in bsp.Leaves)
			{
				var cluster = leaf.Visibility;
				if (cluster < 0 || !skyByCluster.TryGetValue(cluster, out var skyName))
					continue;

				var mins = leaf.Minimums;
				var maxs = leaf.Maximums;
				if (maxs.X <= mins.X || maxs.Y <= mins.Y || maxs.Z <= mins.Z)
					continue; // degenerate/solid leaf

				if (!boxesBySky.TryGetValue(skyName, out var boxes))
					boxesBySky[skyName] = boxes = new List<(Vector3, Vector3)>();
				boxes.Add((mins, maxs));
			}

			if (boxesBySky.Count <= 1)
				return plan;

			plan.DefaultSkyName = PickDefaultSky(skyByCluster, numClusters) ?? fallbackSkyName;

			var totalBoxes = 0;
			foreach (var kvp in boxesBySky)
			{
				var merged = MergeBoxes(kvp.Value);
				totalBoxes += merged.Count;
				plan.Regions.Add(new SkyboxSwapRegion { SkyName = kvp.Key, Boxes = merged });
			}

			logger.Log($"Skybox swap: {plan.Regions.Count} skyboxes, {totalBoxes} trigger brushes, default '{plan.DefaultSkyName}'.");
			return plan;
		}

		// Picks the skybox the map should spawn with: the one visible from the player start's leaf, so the
		// sky is already correct before any trigger fires. Falls back to null (caller uses fallbackSkyName)
		// when there's no start entity or it sees no sky.
		private string? PickDefaultSky(Dictionary<int, string> skyByCluster, int numClusters)
		{
			var start = bsp.Entities.FirstOrDefault(e =>
				e.ClassName == "info_player_start" || e.ClassName == "info_player_deathmatch");
			if (start == null || bsp.Nodes.Count == 0)
				return null;

			var cluster = FindCluster(start.Origin);
			if (cluster < 0 || cluster >= numClusters)
				return null;

			return skyByCluster.TryGetValue(cluster, out var skyName) ? skyName : null;
		}

		// Walks the world BSP tree (model 0, head node 0) down to the leaf containing point, returning its
		// PVS cluster. Positive child index is a node, negative is a leaf (-(index)-1), as in Q3.
		private int FindCluster(Vector3 point)
		{
			var nodeIndex = 0;
			while (nodeIndex >= 0)
			{
				if (nodeIndex >= bsp.Nodes.Count)
					return -1;

				var node = bsp.Nodes[nodeIndex];
				var plane = bsp.Planes[node.PlaneIndex];
				var distance = Vector3.Dot(plane.Normal, point) - plane.Distance;
				nodeIndex = distance >= 0 ? node.Child1Index : node.Child2Index;
			}

			var leafIndex = -(nodeIndex + 1);
			if (leafIndex < 0 || leafIndex >= bsp.Leaves.Count)
				return -1;

			return bsp.Leaves[leafIndex].Visibility;
		}

		// Coalesces a region's per-leaf boxes into fewer, larger boxes so the trigger uses fewer brushes.
		// q3map2 tiles open space into a grid of leaves, so many boxes share a cross-section and abut along
		// one axis; a greedy per-axis sweep merges those runs. Purely a brush-count optimisation - the
		// merged union covers exactly the same space.
		private static List<(Vector3 mins, Vector3 maxs)> MergeBoxes(List<(Vector3 mins, Vector3 maxs)> boxes)
		{
			var current = boxes;
			for (var pass = 0; pass < 2; pass++)
			{
				current = MergeAlongAxis(current, 0);
				current = MergeAlongAxis(current, 1);
				current = MergeAlongAxis(current, 2);
			}
			return current;
		}

		private static List<(Vector3 mins, Vector3 maxs)> MergeAlongAxis(List<(Vector3 mins, Vector3 maxs)> boxes, int axis)
		{
			const float epsilon = 0.5f;
			var a1 = (axis + 1) % 3;
			var a2 = (axis + 2) % 3;

			// Group boxes that share an identical cross-section (the two non-sweep axes), quantised to
			// tolerate float noise; only boxes in the same group can merge along the sweep axis.
			var groups = new Dictionary<(long, long, long, long), List<(Vector3 mins, Vector3 maxs)>>();
			foreach (var box in boxes)
			{
				var key = (Quantize(Get(box.mins, a1)), Quantize(Get(box.maxs, a1)),
					Quantize(Get(box.mins, a2)), Quantize(Get(box.maxs, a2)));
				if (!groups.TryGetValue(key, out var list))
					groups[key] = list = new List<(Vector3, Vector3)>();
				list.Add(box);
			}

			var result = new List<(Vector3 mins, Vector3 maxs)>();
			foreach (var group in groups.Values)
			{
				group.Sort((x, y) => Get(x.mins, axis).CompareTo(Get(y.mins, axis)));

				var run = group[0];
				for (var i = 1; i < group.Count; i++)
				{
					var next = group[i];
					if (Get(next.mins, axis) <= Get(run.maxs, axis) + epsilon)
					{
						// Overlapping/abutting along the sweep axis - extend the run to the farther max.
						if (Get(next.maxs, axis) > Get(run.maxs, axis))
							run.maxs = With(run.maxs, axis, Get(next.maxs, axis));
					}
					else
					{
						result.Add(run);
						run = next;
					}
				}
				result.Add(run);
			}

			return result;
		}

		private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);

		private static Vector3 With(Vector3 v, int axis, float value)
		{
			if (axis == 0) return new Vector3(value, v.Y, v.Z);
			if (axis == 1) return new Vector3(v.X, value, v.Z);
			return new Vector3(v.X, v.Y, value);
		}

		private static long Quantize(float value) => (long)MathF.Round(value * 2f);
	}
}
