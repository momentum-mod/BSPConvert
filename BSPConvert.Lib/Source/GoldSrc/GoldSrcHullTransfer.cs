using LibBSP;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace BSPConvert.Lib.GoldSrc
{
	// Copies a GoldSrc map's clip hulls (see GoldSrcClipHull) into a Source BSP compiled from a converted copy of it,
	// e.g. one that was decompiled, given new visuals and recompiled with VBSP, which only builds collision from the
	// visible brushes. GoldSrc-hull game modes then collide with exactly what the GoldSrc map did, whatever changed
	// visually, as long as the map wasn't moved.
	//
	// Only the lumps that change are rewritten (see SourceBspFile): planes, brushes and brush sides get the hull
	// brushes added, every leaf they overlap lists them, and worldspawn is marked as having GoldSrc clip hulls.
	// Brush entities keep their own hull brushes, matched to the recompiled map's entities by name and bounds.
	public class GoldSrcHullTransfer
	{
		// Source lumps and the versions Strata's VBSP writes, which are the only ones read
		private const int LUMP_ENTITIES = 0;
		private const int LUMP_PLANES = 1;
		private const int LUMP_NODES = 5;
		private const int LUMP_LEAFS = 10;
		private const int LUMP_MODELS = 14;
		private const int LUMP_LEAFBRUSHES = 17;
		private const int LUMP_BRUSHES = 18;
		private const int LUMP_BRUSHSIDES = 19;
		private const int LeafVersion = 2;
		private const int BrushSideVersion = 1;
		private const int LeafBrushVersion = 1;

		private const int BspVersion = 25;
		private const int PlaneSize = 20;
		private const int LeafSize = 56;
		private const int LeafFirstBrushOffset = 44;
		private const int ModelSize = 48;
		private const int BrushSize = 12;
		private const int BrushSideSize = 16;

		// Strata's map limits for the lumps that grow (as vbspinfo reports them)
		private const int MaxBrushes = 131072;
		private const int MaxBrushSides = 1310720;
		private const int MaxPlanes = 1048576;
		private const int MaxLeafBrushes = 1048576;

		// How far apart (per bound, in units) an entity's model bounds can be in the two maps and still match. VBSP
		// and the GoldSrc compilers bound models a little differently.
		private const float BoundsTolerance = 2f;

		// GoldSrc brush entities whose clip hulls the player never collides with, which don't need a match
		private static readonly HashSet<string> NonSolidClasses = new HashSet<string>
		{
			"func_illusionary", "func_water", "func_buyzone", "func_bomb_target", "func_hostage_rescue",
			"func_vip_safetyzone", "func_escapezone", "func_friction", "func_mortar_field",
		};

		// GoldSrc brush entities that never move, whose clip hulls go in the world if the porter made them world
		// brushes
		private static readonly HashSet<string> StaticClasses = new HashSet<string> { "func_wall" };

		private readonly ILogger logger;

		private SourceBspFile target = null!;
		private GoldSrcBsp gs = null!;
		private readonly List<(Vector3 normal, float dist)> planes = new List<(Vector3, float)>();
		private readonly Dictionary<(Vector3, float), int> planeLookup = new Dictionary<(Vector3, float), int>();
		private (int planeIndex, int child0, int child1)[] nodes = Array.Empty<(int, int, int)>();
		private readonly MemoryStream newBrushSides = new MemoryStream();
		private readonly MemoryStream newBrushes = new MemoryStream();
		private int brushCount;
		private int brushSideCount;
		private readonly Dictionary<int, List<int>> addedLeafBrushes = new Dictionary<int, List<int>>();

		public GoldSrcHullTransfer(ILogger logger)
		{
			this.logger = logger;
		}

		public bool Transfer(string goldSrcBspPath, string targetBspPath, string outputPath)
		{
			var goldSrcBsp = new BSP(new FileInfo(goldSrcBspPath));
			if (!goldSrcBsp.MapType.IsSubtypeOf(MapType.GoldSrc))
			{
				logger.Log($"Error: {goldSrcBspPath} isn't a GoldSrc BSP");
				return false;
			}

			var targetFile = SourceBspFile.Read(targetBspPath);
			if (targetFile == null || targetFile.Version != BspVersion)
			{
				logger.Log($"Error: {targetBspPath} isn't a Strata Source BSP (version {BspVersion})");
				return false;
			}

			if (targetFile.GetLumpVersion(LUMP_LEAFS) != LeafVersion || targetFile.GetLumpVersion(LUMP_BRUSHSIDES) != BrushSideVersion ||
				targetFile.GetLumpVersion(LUMP_LEAFBRUSHES) != LeafBrushVersion)
			{
				logger.Log($"Error: {targetBspPath} has older leaf or brush lumps than Strata's VBSP writes");
				return false;
			}

			target = targetFile;
			gs = GoldSrcBsp.Read(goldSrcBsp);

			var targetEntities = ParseEntities(target.GetEntities());
			if (targetEntities.Count == 0 || targetEntities[0].GetValueOrDefault("classname") != "worldspawn")
			{
				logger.Log($"Error: {targetBspPath} has no worldspawn");
				return false;
			}

			// The worldspawn key survives decompiling and recompiling a converted map, but the hull brushes don't
			if (HasHullBrushes())
			{
				logger.Log($"Error: {targetBspPath} already has GoldSrc clip hulls");
				return false;
			}

			logger.Log($"Transferring the clip hulls of {Path.GetFileName(goldSrcBspPath)} to {Path.GetFileName(targetBspPath)}...");
			ReadPlanes();
			ReadNodes();
			var models = ReadModels();
			brushCount = target.GetLump(LUMP_BRUSHES).Length / BrushSize;
			brushSideCount = target.GetLump(LUMP_BRUSHSIDES).Length / BrushSideSize;

			var hullBrushCounts = new int[GoldSrcBsp.MAX_MAP_HULLS];
			foreach (var (gsModel, targetModel, offset, isLadder) in MatchModels(targetEntities, models))
			{
				foreach (var (hull, hullExtents, hullContents) in GoldSrcClipHull.PlayerHulls)
				{
					var contents = (int)hullContents | (isLadder ? (int)SourceContentsFlags.CONTENTS_LADDER : 0);
					foreach (var brush in GoldSrcClipHull.GetBrushes(gs, gsModel, hull, hullExtents))
					{
						AddBrush(brush, offset, contents, models[targetModel].headNode);
						hullBrushCounts[hull]++;
					}
				}
			}

			WriteLumps(!targetEntities[0].ContainsKey(GoldSrcConverter.ClipHullsWorldspawnKey));
			target.Write(outputPath);

			foreach (var (hull, _, _) in GoldSrcClipHull.PlayerHulls)
				logger.Log($"Added {hullBrushCounts[hull]} hull {hull} brushes");

			WarnIfOverLimit("brushes", brushCount, MaxBrushes);
			WarnIfOverLimit("brush sides", brushSideCount, MaxBrushSides);
			WarnIfOverLimit("planes", planes.Count, MaxPlanes);
			WarnIfOverLimit("leaf brushes", target.GetLump(LUMP_LEAFBRUSHES).Length / 4, MaxLeafBrushes);

			return true;
		}

		#region Entity matching

		// Pairs each GoldSrc model with the recompiled map's model its clip hulls go in, and the offset from the
		// GoldSrc model's space to that one's (models are built around their entity's origin in both).
		private List<(int gsModel, int targetModel, Vector3 offset, bool isLadder)> MatchModels(List<Dictionary<string, string>> targetEntities, (Vector3 mins, Vector3 maxs, int headNode)[] models)
		{
			var matches = new List<(int, int, Vector3, bool)> { (0, 0, Vector3.Zero, false) };

			var candidates = new List<(int model, string targetName, Vector3 origin, Vector3 mins, Vector3 maxs)>();
			foreach (var entity in targetEntities)
			{
				if (TryGetModelIndex(entity.GetValueOrDefault("model"), out var modelIndex) && modelIndex > 0 && modelIndex < models.Length)
				{
					var origin = ParseVector(entity.GetValueOrDefault("origin"));
					candidates.Add((modelIndex, entity.GetValueOrDefault("targetname") ?? "", origin, models[modelIndex].mins + origin, models[modelIndex].maxs + origin));
				}
			}

			var used = new HashSet<int>();
			var unmatched = new List<string>();
			foreach (var entity in gs.Entities)
			{
				if (!TryGetModelIndex(entity["model"], out var gsModel) || gsModel <= 0 || gsModel >= gs.Models.Length)
					continue;

				var origin = ParseVector(entity["origin"]);
				var className = entity.ClassName;

				// Ladders are world brushes in converted maps (see GoldSrcConverter.ConvertLadder)
				if (className == "func_ladder")
				{
					matches.Add((gsModel, 0, origin, true));
					continue;
				}

				var mins = gs.Models[gsModel].mins + origin;
				var maxs = gs.Models[gsModel].maxs + origin;
				var targetName = entity["targetname"];

				// An entity of the same name is the match wherever it is; otherwise the one in the same place
				int? match = null;
				var matchDistance = float.MaxValue;
				for (var i = 0; i < candidates.Count; i++)
				{
					var candidate = candidates[i];
					if (used.Contains(candidate.model))
						continue;

					var distance = MathF.Max(MaxComponent(Vector3.Abs(candidate.mins - mins)), MaxComponent(Vector3.Abs(candidate.maxs - maxs)));
					var isMatch = targetName.Length > 0 ? candidate.targetName == targetName : distance <= BoundsTolerance;
					if (isMatch && distance < matchDistance)
						(match, matchDistance) = (i, distance);
				}

				if (match is int index)
				{
					var candidate = candidates[index];
					used.Add(candidate.model);
					matches.Add((gsModel, candidate.model, origin - candidate.origin, false));
				}
				else if (StaticClasses.Contains(className))
				{
					// Made part of the world
					matches.Add((gsModel, 0, origin, false));
				}
				else if (!NonSolidClasses.Contains(className) && !className.StartsWith("trigger_", StringComparison.Ordinal))
				{
					unmatched.Add(targetName.Length > 0 ? $"{className} \"{targetName}\"" : $"{className} at {entity["origin"]} (model *{gsModel})");
				}
			}

			logger.Log($"Matched {matches.Count - 1} GoldSrc brush entities");
			foreach (var name in unmatched)
				logger.Log($"Warning: No entity matches GoldSrc {name}, so it has no GoldSrc collision");

			return matches;
		}

		private static float MaxComponent(Vector3 v)
		{
			return MathF.Max(v.X, MathF.Max(v.Y, v.Z));
		}

		private static bool TryGetModelIndex(string? model, out int index)
		{
			index = -1;
			return model != null && model.StartsWith('*') && int.TryParse(model.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
		}

		private static Vector3 ParseVector(string? value)
		{
			var parts = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 3)
				return Vector3.Zero;

			float Parse(string part) => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
			return new Vector3(Parse(parts[0]), Parse(parts[1]), Parse(parts[2]));
		}

		// The entity lump's entities as key values, in order (worldspawn first)
		private static List<Dictionary<string, string>> ParseEntities(string text)
		{
			var entities = new List<Dictionary<string, string>>();
			foreach (Match block in Regex.Matches(text, @"\{([^{}]*)\}"))
			{
				var entity = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (Match pair in Regex.Matches(block.Groups[1].Value, "\"([^\"]*)\"\\s*\"([^\"]*)\""))
					entity.TryAdd(pair.Groups[1].Value, pair.Groups[2].Value);

				entities.Add(entity);
			}

			return entities;
		}

		#endregion

		#region Lumps

		private void ReadPlanes()
		{
			var lump = target.GetLump(LUMP_PLANES);
			for (var offset = 0; offset + PlaneSize <= lump.Length; offset += PlaneSize)
			{
				var normal = new Vector3(BitConverter.ToSingle(lump, offset), BitConverter.ToSingle(lump, offset + 4), BitConverter.ToSingle(lump, offset + 8));
				var dist = BitConverter.ToSingle(lump, offset + 12);
				planeLookup.TryAdd((normal, dist), planes.Count);
				planes.Add((normal, dist));
			}
		}

		private void ReadNodes()
		{
			var lump = target.GetLump(LUMP_NODES);
			var nodeSize = target.GetLumpVersion(LUMP_NODES) >= 1 ? 48 : 32;
			nodes = new (int, int, int)[lump.Length / nodeSize];
			for (var i = 0; i < nodes.Length; i++)
				nodes[i] = (BitConverter.ToInt32(lump, i * nodeSize), BitConverter.ToInt32(lump, i * nodeSize + 4), BitConverter.ToInt32(lump, i * nodeSize + 8));
		}

		private (Vector3 mins, Vector3 maxs, int headNode)[] ReadModels()
		{
			var lump = target.GetLump(LUMP_MODELS);
			var models = new (Vector3, Vector3, int)[lump.Length / ModelSize];
			for (var i = 0; i < models.Length; i++)
			{
				var offset = i * ModelSize;
				Vector3 Read(int at) => new Vector3(BitConverter.ToSingle(lump, at), BitConverter.ToSingle(lump, at + 4), BitConverter.ToSingle(lump, at + 8));
				models[i] = (Read(offset), Read(offset + 12), BitConverter.ToInt32(lump, offset + 36));
			}

			return models;
		}

		private int AddPlane(Vector3 normal, float dist)
		{
			if (planeLookup.TryGetValue((normal, dist), out var index))
				return index;

			index = planes.Count;
			planes.Add((normal, dist));
			planeLookup[(normal, dist)] = index;
			return index;
		}

		// Adds a hull brush, moved by offset, and lists it in the leaves of the model's tree its region overlaps
		private void AddBrush(GoldSrcClipHull.Brush brush, Vector3 offset, int contents, int headNode)
		{
			using (var writer = new BinaryWriter(newBrushSides, Encoding.ASCII, true))
			{
				foreach (var side in brush.Sides)
				{
					writer.Write((uint)AddPlane(side.Normal, side.Dist + Vector3.Dot(side.Normal, offset)));
					writer.Write(-1); // texinfo
					writer.Write(0); // dispinfo
					writer.Write((byte)0); // bevel
					writer.Write((byte)0); // thin
					writer.Write((short)0); // padding
				}
			}

			using (var writer = new BinaryWriter(newBrushes, Encoding.ASCII, true))
			{
				writer.Write(brushSideCount);
				writer.Write(brush.Sides.Count);
				writer.Write(contents);
			}

			brushSideCount += brush.Sides.Count;
			var vertices = brush.Region.Vertices.Select(v => v + offset).ToArray();
			RegisterInLeaves(headNode, vertices, brushCount++);
		}

		// A box trace only tests the brushes of leaves it passes through, and its center is somewhere inside the clip
		// hull region whenever it collides with the brush, so the brush is listed in every leaf the region overlaps
		private void RegisterInLeaves(int nodeIndex, Vector3[] vertices, int brushIndex)
		{
			while (true)
			{
				if (nodeIndex < 0)
				{
					var leafIndex = -nodeIndex - 1;
					if (!addedLeafBrushes.TryGetValue(leafIndex, out var brushes))
						addedLeafBrushes[leafIndex] = brushes = new List<int>();

					brushes.Add(brushIndex);
					return;
				}

				var (planeIndex, child0, child1) = nodes[nodeIndex];
				var (normal, dist) = planes[planeIndex];
				bool front = false, back = false;
				foreach (var v in vertices)
				{
					var d = Vector3.Dot(normal, v) - dist;
					front |= d > GoldSrcConverter.RegistrationEpsilon;
					back |= d < -GoldSrcConverter.RegistrationEpsilon;
				}

				// Flat against the plane
				if (!front && !back)
					front = back = true;

				if (front && back)
				{
					RegisterInLeaves(child0, vertices, brushIndex);
					nodeIndex = child1;
				}
				else
				{
					nodeIndex = front ? child0 : child1;
				}
			}
		}

		private void WarnIfOverLimit(string name, int count, int limit)
		{
			if (count > limit)
				logger.Log($"Warning: The map has {count} {name}, more than Strata's limit of {limit}, so it may not load");
		}

		private bool HasHullBrushes()
		{
			const int hullContents = (int)(SourceContentsFlags.CONTENTS_GOLDSRC_HULL_STANDING | SourceContentsFlags.CONTENTS_GOLDSRC_HULL_DUCKING);
			var brushes = target.GetLump(LUMP_BRUSHES);
			for (var offset = 0; offset + BrushSize <= brushes.Length; offset += BrushSize)
			{
				if ((BitConverter.ToInt32(brushes, offset + 8) & hullContents) != 0)
					return true;
			}

			return false;
		}

		private void WriteLumps(bool addWorldspawnKey)
		{
			var planeLump = new byte[planes.Count * PlaneSize];
			for (var i = 0; i < planes.Count; i++)
			{
				var (normal, dist) = planes[i];
				var offset = i * PlaneSize;
				BitConverter.GetBytes(normal.X).CopyTo(planeLump, offset);
				BitConverter.GetBytes(normal.Y).CopyTo(planeLump, offset + 4);
				BitConverter.GetBytes(normal.Z).CopyTo(planeLump, offset + 8);
				BitConverter.GetBytes(dist).CopyTo(planeLump, offset + 12);
				BitConverter.GetBytes((int)SourceBspBuilder.GetPlaneAxis(normal)).CopyTo(planeLump, offset + 16);
			}

			// The original planes are kept as they were, axis type included
			var originalPlanes = target.GetLump(LUMP_PLANES);
			Buffer.BlockCopy(originalPlanes, 0, planeLump, 0, originalPlanes.Length);
			target.SetLump(LUMP_PLANES, planeLump, target.GetLumpVersion(LUMP_PLANES));

			target.SetLump(LUMP_BRUSHES, Concat(target.GetLump(LUMP_BRUSHES), newBrushes.ToArray()), target.GetLumpVersion(LUMP_BRUSHES));
			target.SetLump(LUMP_BRUSHSIDES, Concat(target.GetLump(LUMP_BRUSHSIDES), newBrushSides.ToArray()), target.GetLumpVersion(LUMP_BRUSHSIDES));

			// Each leaf's brush list is rewritten with the new brushes after its own
			var leaves = target.GetLump(LUMP_LEAFS);
			var oldLeafBrushes = target.GetLump(LUMP_LEAFBRUSHES);
			using var leafBrushes = new MemoryStream();
			using (var writer = new BinaryWriter(leafBrushes, Encoding.ASCII, true))
			{
				for (var leafIndex = 0; leafIndex < leaves.Length / LeafSize; leafIndex++)
				{
					var offset = leafIndex * LeafSize + LeafFirstBrushOffset;
					var first = BitConverter.ToInt32(leaves, offset);
					var count = BitConverter.ToInt32(leaves, offset + 4);
					BitConverter.GetBytes((int)(leafBrushes.Length / 4)).CopyTo(leaves, offset);

					for (var i = 0; i < count; i++)
						writer.Write(BitConverter.ToUInt32(oldLeafBrushes, (first + i) * 4));

					if (addedLeafBrushes.TryGetValue(leafIndex, out var added))
					{
						foreach (var brushIndex in added)
							writer.Write((uint)brushIndex);

						count += added.Count;
					}

					BitConverter.GetBytes(count).CopyTo(leaves, offset + 4);
				}
			}

			target.SetLump(LUMP_LEAFS, leaves, LeafVersion);
			target.SetLump(LUMP_LEAFBRUSHES, leafBrushes.ToArray(), LeafBrushVersion);

			// Marks the map for GoldSrc-hull game modes, in worldspawn's key values
			if (addWorldspawnKey)
			{
				var entities = target.GetEntities();
				var worldspawnStart = entities.IndexOf('{', StringComparison.Ordinal) + 1;
				target.SetEntities(entities.Insert(worldspawnStart, $"\n\"{GoldSrcConverter.ClipHullsWorldspawnKey}\" \"1\""));
			}
		}

		private static byte[] Concat(byte[] first, byte[] second)
		{
			var result = new byte[first.Length + second.Length];
			Buffer.BlockCopy(first, 0, result, 0, first.Length);
			Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
			return result;
		}

		#endregion
	}
}
