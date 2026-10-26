using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BSPConvert.Lib.GoldSrc
{
	// GoldSrc doesn't expand the player box at runtime. The compiler bakes a clip hull per player size (hull 1
	// standing, hull 3 ducking) and the engine traces a point through it. Each solid clip hull leaf becomes a brush
	// whose planes are pulled in by the player box: Source's box trace pushes every brush plane back out by the box
	// (CM_ClipBoxToBrush), which lands it exactly on the clip hull plane. The brushes carry the hull's own contents
	// bit, because a hull's brushes only line up for the box size they were shrunk for; GoldSrc-hull game modes pick
	// the bit by the size of the box they trace.
	//
	// Modes with a bigger box than the hull trace the same brushes, which then push each plane out by the extra size
	// too. That only grows each region by the extra size (its Minkowski sum with the extra box) if the brush also has
	// the region's bevel planes, like VBSP gives every brush. Without them a thin region whose end is a sharp edge
	// between two sloped planes stretches out far along that edge (kz_lain's cave mouth grew a floor 60 units past
	// the ledge for Bhop's taller box).
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
		// How close a bevel's normal can be to a face's or another bevel's to count as the same plane
		private const float BevelNormalEpsilon = 0.0001f;
		// Distance from a bevel plane within which an edge counts as touching it
		private const float BevelEdgeEpsilon = 0.01f;

		// A solid clip hull region: where the player's origin can't be, the brush sides that block it there, and the
		// bevel sides that keep it from overgrowing for bigger boxes
		public readonly record struct Brush(ConvexRegion Region, IReadOnlyList<HalfSpace> Sides, IReadOnlyList<HalfSpace> Bevels);

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
					brushes.Add(new Brush(region, GetShrunkSides(region, hullExtents), GetShrunkBevels(region, hullExtents)));

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
				sides.Add(Shrink(face.Normal, face.Dist, hullExtents));

			return sides;
		}

		// The region's bevel planes, shrunk like its sides: the axial planes and the planes through each edge parallel
		// to an axis that touch the region without cutting it. Those and the faces are every plane of the region's
		// Minkowski sum with a box. They touch the region, so at the hull's own size they change nothing.
		private static List<HalfSpace> GetShrunkBevels(ConvexRegion region, Vector3 hullExtents)
		{
			var normals = new List<Vector3>();
			bool IsNew(Vector3 normal) =>
				!region.Faces.Any(face => Vector3.Dot(face.Normal, normal) > 1f - BevelNormalEpsilon) &&
				!normals.Any(other => Vector3.Dot(other, normal) > 1f - BevelNormalEpsilon);

			for (var axis = 0; axis < 3; axis++)
			{
				for (var sign = -1; sign <= 1; sign += 2)
				{
					var normal = Vector3.Zero;
					normal[axis] = sign;
					if (IsNew(normal))
						normals.Add(normal);
				}
			}

			foreach (var winding in region.Windings)
			{
				for (var i = 0; i < winding.Count; i++)
				{
					var a = winding[i];
					var edge = winding[(i + 1) % winding.Count] - a;
					for (var axis = 0; axis < 3; axis++)
					{
						var axisVector = Vector3.Zero;
						axisVector[axis] = 1f;
						var cross = Vector3.Cross(edge, axisVector);
						if (cross.Length() < BevelEdgeEpsilon)
							continue;

						// Whichever way round has the edge on the region's outermost point touches it there
						for (var sign = -1; sign <= 1; sign += 2)
						{
							var normal = Vector3.Normalize(cross) * sign;
							if (Vector3.Dot(normal, a) >= GetSupport(region, normal) - BevelEdgeEpsilon && IsNew(normal))
								normals.Add(normal);
						}
					}
				}
			}

			return normals.Select(normal => Shrink(normal, GetSupport(region, normal), hullExtents)).ToList();
		}

		// How far the region reaches along a direction
		private static float GetSupport(ConvexRegion region, Vector3 normal)
		{
			return region.Vertices.Max(v => Vector3.Dot(normal, v));
		}

		private static HalfSpace Shrink(Vector3 n, float dist, Vector3 hullExtents)
		{
			var offset = MathF.Abs(n.X) * hullExtents.X + MathF.Abs(n.Y) * hullExtents.Y + MathF.Abs(n.Z) * hullExtents.Z;
			return new HalfSpace(n, dist - offset);
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
