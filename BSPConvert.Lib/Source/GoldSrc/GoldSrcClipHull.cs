using System;
using System.Collections.Generic;
using System.Numerics;

namespace BSPConvert.Lib.GoldSrc
{
	// GoldSrc doesn't expand the player box at runtime. The compiler bakes a clip hull per player size (hull 1
	// standing, hull 3 ducking) and the engine traces a point through it. Each solid clip hull leaf becomes a brush
	// whose planes are pulled in by the player box: Source's box trace pushes every brush plane back out by the box
	// (CM_ClipBoxToBrush), which lands it exactly on the clip hull plane. The brushes carry the hull's own contents
	// bit, because a hull's brushes only line up for the box size they were shrunk for; GoldSrc-hull game modes pick
	// the bit by the size of the box they trace.
	public static class GoldSrcClipHull
	{
		// Half extents of the GoldSrc player hulls (Momentum's GoldSrc-hull game modes use the same sizes)
		public static readonly Vector3 StandingHullExtents = new Vector3(16f, 16f, 36f);
		public static readonly Vector3 DuckingHullExtents = new Vector3(16f, 16f, 18f);

		public const int StandingHull = 1;
		public const int DuckingHull = 3;

		// The player hulls with their extents and the contents their brushes carry
		public static readonly (int hull, Vector3 extents, SourceContentsFlags contents)[] PlayerHulls =
		{
			(StandingHull, StandingHullExtents, SourceContentsFlags.CONTENTS_GOLDSRC_HULL_STANDING),
			(DuckingHull, DuckingHullExtents, SourceContentsFlags.CONTENTS_GOLDSRC_HULL_DUCKING),
		};

		// Padding around a model's bounds when closing off the outermost BSP regions
		private const float BoundsPadding = 1f;

		// A solid clip hull region: where the player's origin can't be, and the brush sides that block it there
		public readonly record struct Brush(ConvexRegion Region, IReadOnlyList<HalfSpace> Sides);

		// The brushes of one of a model's clip hulls
		public static List<Brush> GetBrushes(GoldSrcBsp gs, int modelIndex, int hull, Vector3 hullExtents)
		{
			var brushes = new List<Brush>();
			var model = gs.Models[modelIndex];

			// Clip hull planes sit up to the hull's extents outside the model's visible bounds
			var path = GetBoundsHalfSpaces(model.mins - hullExtents, model.maxs + hullExtents, BoundsPadding);
			Walk(gs, model.headNodes[hull], path, hullExtents, brushes);
			return brushes;
		}

		private static void Walk(GoldSrcBsp gs, int clipNodeIndex, List<HalfSpace> path, Vector3 hullExtents, List<Brush> brushes)
		{
			if (clipNodeIndex < 0)
			{
				// Clip hulls only distinguish solid from non-solid (water etc. is empty to the player hulls)
				if (clipNodeIndex == GoldSrcBsp.CONTENTS_SOLID && ConvexRegion.Create(path) is ConvexRegion region)
					brushes.Add(new Brush(region, GetShrunkSides(region, hullExtents)));

				return;
			}

			// Compilers can leave a model with no clip hull pointing one past the last clipnode (bkz_junglebhop's last
			// model does). The engine never traces it, so it has no brushes.
			if (clipNodeIndex >= gs.ClipNodes.Length)
				return;

			var clipNode = gs.ClipNodes[clipNodeIndex];
			var plane = gs.Planes[clipNode.planeIndex];

			path.Add(new HalfSpace(-plane.normal, -plane.dist));
			Walk(gs, clipNode.child0, path, hullExtents, brushes);
			path[path.Count - 1] = new HalfSpace(plane.normal, plane.dist);
			Walk(gs, clipNode.child1, path, hullExtents, brushes);
			path.RemoveAt(path.Count - 1);
		}

		// Pulls each plane in by the box's extent along its normal, which Source's box trace adds back
		private static List<HalfSpace> GetShrunkSides(ConvexRegion region, Vector3 hullExtents)
		{
			var sides = new List<HalfSpace>(region.Faces.Count);
			foreach (var face in region.Faces)
			{
				var n = face.Normal;
				var offset = MathF.Abs(n.X) * hullExtents.X + MathF.Abs(n.Y) * hullExtents.Y + MathF.Abs(n.Z) * hullExtents.Z;
				sides.Add(new HalfSpace(n, face.Dist - offset));
			}

			return sides;
		}

		// A model's bounds as half-spaces, closing off the regions at the edge of its BSP tree
		public static List<HalfSpace> GetBoundsHalfSpaces(Vector3 mins, Vector3 maxs, float padding)
		{
			return new List<HalfSpace>
			{
				new HalfSpace(new Vector3(1, 0, 0), maxs.X + padding),
				new HalfSpace(new Vector3(-1, 0, 0), -(mins.X - padding)),
				new HalfSpace(new Vector3(0, 1, 0), maxs.Y + padding),
				new HalfSpace(new Vector3(0, -1, 0), -(mins.Y - padding)),
				new HalfSpace(new Vector3(0, 0, 1), maxs.Z + padding),
				new HalfSpace(new Vector3(0, 0, -1), -(mins.Z - padding))
			};
		}
	}
}
