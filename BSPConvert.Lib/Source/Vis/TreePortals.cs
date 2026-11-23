using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BSPConvert.Lib.Vis
{
	// A portal between two clusters: a convex polygon whose winding faces the front cluster
	public readonly record struct ClusterPortal(int FrontCluster, int BackCluster, Vector3d[] Points);

	// The portals between a tree's clusters, and the clusters open to the outside of the tree's bounds
	public class ClusterPortals
	{
		public required int NumClusters;
		public required List<ClusterPortal> Portals;
		public required HashSet<int> OutsideClusters;

		// The clusters open to the outside that no portals connect to an occupied one: the void around a map that leaked
		// when it was compiled, which the rest of the map can't see into
		public HashSet<int> FindVoid(ISet<int> occupiedClusters)
		{
			var components = new int[NumClusters];
			for (var i = 0; i < components.Length; i++)
				components[i] = i;

			int Find(int cluster)
			{
				while (components[cluster] != cluster)
					cluster = components[cluster] = components[components[cluster]];
				return cluster;
			}

			foreach (var portal in Portals)
				components[Find(portal.FrontCluster)] = Find(portal.BackCluster);

			var open = new HashSet<int>(OutsideClusters.Select(Find));
			open.ExceptWith(occupiedClusters.Select(Find));

			var result = new HashSet<int>();
			for (var cluster = 0; cluster < NumClusters; cluster++)
			{
				if (open.Contains(Find(cluster)))
					result.Add(cluster);
			}

			return result;
		}
	}

	// Rebuilds the portals between a BSP tree's leaves like q3map2's MakeTreePortals, for vvis to compute the PVS from
	public class TreePortals
	{
		public readonly record struct TreePlane(Vector3d Normal, double Distance);

		// A node's children are other nodes (>= 0) or leaves (-(leaf + 1)), like Quake and Source BSP nodes
		public readonly record struct TreeNode(int PlaneIndex, int Front, int Back);

		// Same epsilons as q3map2
		private const double ClipEpsilon = 0.1;
		private const double SplitEpsilon = 0.001;
		private const double BaseWindingEpsilon = 0.001;
		private const double TinyEdgeLength = 0.2;
		// Room around the world's bounds, so portals near its edges aren't clipped by the outside box
		private const double OutsideSpace = 8;
		// vvis's MAX_POINTS_ON_WINDING
		private const int MaxPortalPoints = 64;

		private class Portal
		{
			public required TreePlane Plane;
			public required Winding Winding;
			// Front and back side of Plane: node ids (see NodeId), or Outside
			public int Front, Back;
		}

		private const int Outside = -1;

		private readonly IReadOnlyList<TreePlane> planes;
		private readonly IReadOnlyList<TreeNode> nodes;
		// Portals bounding each node and leaf, indexed by NodeId
		private readonly List<Portal>[] nodePortals;

		private TreePortals(IReadOnlyList<TreePlane> planes, IReadOnlyList<TreeNode> nodes, int numLeaves)
		{
			this.planes = planes;
			this.nodes = nodes;

			nodePortals = new List<Portal>[nodes.Count + numLeaves];
			for (var i = 0; i < nodePortals.Length; i++)
				nodePortals[i] = new List<Portal>();
		}

		// The portals between clusters of the tree under headNode, which fills mins to maxs. leafClusters gives each
		// leaf's cluster, -1 for solid leaves, which portals don't lead into.
		public static ClusterPortals Build(IReadOnlyList<TreePlane> planes, IReadOnlyList<TreeNode> nodes, int headNode,
			IReadOnlyList<int> leafClusters, Vector3d mins, Vector3d maxs)
		{
			var tree = new TreePortals(planes, nodes, leafClusters.Count);
			tree.MakeHeadNodePortals(headNode, mins, maxs);
			tree.MakeTreePortals(headNode);

			return new ClusterPortals
			{
				NumClusters = leafClusters.DefaultIfEmpty(-1).Max() + 1,
				Portals = tree.GetClusterPortals(leafClusters),
				OutsideClusters = tree.GetOutsideClusters(leafClusters),
			};
		}

		// The leaf the point is in
		public static int FindLeaf(IReadOnlyList<TreePlane> planes, IReadOnlyList<TreeNode> nodes, int headNode, Vector3d point)
		{
			var child = headNode;
			while (child >= 0)
			{
				var node = nodes[child];
				var plane = planes[node.PlaneIndex];
				child = Vector3d.Dot(plane.Normal, point) - plane.Distance >= 0 ? node.Front : node.Back;
			}

			return -child - 1;
		}

		private int NodeId(int child) => child >= 0 ? child : nodes.Count + (-child - 1);

		private bool IsLeaf(int id) => id >= nodes.Count;

		// The box around the world, whose sides lead to the outside
		private void MakeHeadNodePortals(int headNode, Vector3d mins, Vector3d maxs)
		{
			var head = NodeId(headNode);
			for (var axis = 0; axis < 3; axis++)
			{
				for (var side = 0; side < 2; side++)
				{
					var normal = Vector3d.Zero.With(axis, side == 0 ? 1 : -1);
					var distance = side == 0 ? maxs[axis] + OutsideSpace : -(mins[axis] - OutsideSpace);
					var portal = new Portal
					{
						Plane = new TreePlane(normal, distance),
						Winding = Winding.BaseForPlane(normal, distance),
						// Normals point out of the box, at the outside
						Front = Outside,
						Back = head,
					};
					nodePortals[head].Add(portal);
				}
			}

			// Clip each side by the others
			var sides = nodePortals[head].ToArray();
			for (var i = 0; i < sides.Length; i++)
			{
				for (var j = 0; j < sides.Length; j++)
				{
					if (i != j)
						sides[i].Winding = sides[i].Winding.Chop(-sides[j].Plane.Normal, -sides[j].Plane.Distance, ClipEpsilon)!;
				}
			}
		}

		// Depth first without recursion, since trees can be deeper than the stack allows
		private void MakeTreePortals(int headNode)
		{
			// The nodes from the head node to the current one's parent, and which side of each it's on
			var path = new List<(int node, bool front)>();
			var work = new Stack<(int node, int depth, bool front)>();
			work.Push((headNode, 0, true));
			while (work.Count > 0)
			{
				var (node, depth, front) = work.Pop();
				if (path.Count > depth)
					path.RemoveRange(depth, path.Count - depth);
				if (depth > 0)
					path[depth - 1] = (path[depth - 1].node, front);

				MakeNodePortal(node, path);
				SplitNodePortals(node);

				path.Add((node, true));
				var treeNode = nodes[node];
				if (treeNode.Back >= 0)
					work.Push((treeNode.Back, depth + 1, false));
				if (treeNode.Front >= 0)
					work.Push((treeNode.Front, depth + 1, true));
			}
		}

		// A winding covering the node's region of its plane: the plane clipped by every parent's plane
		private Winding? BaseWindingForNode(int node, List<(int node, bool front)> parents)
		{
			var plane = planes[nodes[node].PlaneIndex];
			var winding = Winding.BaseForPlane(plane.Normal, plane.Distance);
			for (var i = parents.Count - 1; i >= 0 && winding != null; i--)
			{
				var parentPlane = planes[nodes[parents[i].node].PlaneIndex];
				winding = parents[i].front ?
					winding.Chop(parentPlane.Normal, parentPlane.Distance, BaseWindingEpsilon) :
					winding.Chop(-parentPlane.Normal, -parentPlane.Distance, BaseWindingEpsilon);
			}

			return winding;
		}

		private void MakeNodePortal(int node, List<(int node, bool front)> parents)
		{
			var winding = BaseWindingForNode(node, parents);

			// Clip it by the portals bounding the node, keeping the inside
			var id = NodeId(node);
			foreach (var portal in nodePortals[id])
			{
				if (winding == null)
					break;

				winding = portal.Front == id ?
					winding.Chop(portal.Plane.Normal, portal.Plane.Distance, ClipEpsilon) :
					winding.Chop(-portal.Plane.Normal, -portal.Plane.Distance, ClipEpsilon);
			}

			if (winding == null || winding.IsTiny(TinyEdgeLength))
				return;

			var treeNode = nodes[node];
			AddPortal(new Portal
			{
				Plane = planes[treeNode.PlaneIndex],
				Winding = winding,
				Front = NodeId(treeNode.Front),
				Back = NodeId(treeNode.Back),
			});
		}

		// Hands the portals bounding the node to its children, splitting those its plane crosses
		private void SplitNodePortals(int node)
		{
			var id = NodeId(node);
			var treeNode = nodes[node];
			var plane = planes[treeNode.PlaneIndex];
			var front = NodeId(treeNode.Front);
			var back = NodeId(treeNode.Back);

			var portals = nodePortals[id].ToArray();
			foreach (var portal in portals)
			{
				var side = portal.Front == id ? 0 : 1;
				var other = side == 0 ? portal.Back : portal.Front;
				RemovePortal(portal);

				portal.Winding.Split(plane.Normal, plane.Distance, SplitEpsilon, out var frontWinding, out var backWinding);
				if (frontWinding != null && frontWinding.IsTiny(TinyEdgeLength))
					frontWinding = null;
				if (backWinding != null && backWinding.IsTiny(TinyEdgeLength))
					backWinding = null;

				if (frontWinding == null && backWinding == null)
					continue;

				if (frontWinding == null || backWinding == null)
				{
					// Entirely on one side, so it keeps its winding
					var child = frontWinding != null ? front : back;
					SetSides(portal, side, child, other);
					AddPortal(portal);
					continue;
				}

				var backPortal = new Portal { Plane = portal.Plane, Winding = backWinding };
				portal.Winding = frontWinding;
				SetSides(portal, side, front, other);
				SetSides(backPortal, side, back, other);
				AddPortal(portal);
				AddPortal(backPortal);
			}

			nodePortals[id].Clear();
		}

		private static void SetSides(Portal portal, int side, int child, int other)
		{
			if (side == 0)
			{
				portal.Front = child;
				portal.Back = other;
			}
			else
			{
				portal.Front = other;
				portal.Back = child;
			}
		}

		private void AddPortal(Portal portal)
		{
			if (portal.Front != Outside)
				nodePortals[portal.Front].Add(portal);
			if (portal.Back != Outside)
				nodePortals[portal.Back].Add(portal);
		}

		private void RemovePortal(Portal portal)
		{
			if (portal.Front != Outside)
				nodePortals[portal.Front].Remove(portal);
			if (portal.Back != Outside)
				nodePortals[portal.Back].Remove(portal);
		}

		// The leaves' portals that lead from one cluster to another
		private List<ClusterPortal> GetClusterPortals(IReadOnlyList<int> leafClusters)
		{
			var result = new List<ClusterPortal>();
			for (var id = nodes.Count; id < nodePortals.Length; id++)
			{
				foreach (var portal in nodePortals[id])
				{
					// Each portal is listed by both its leaves, so take it from its front one
					if (portal.Front != id || portal.Back == Outside || !IsLeaf(portal.Back))
						continue;

					var frontCluster = leafClusters[portal.Front - nodes.Count];
					var backCluster = leafClusters[portal.Back - nodes.Count];
					if (frontCluster < 0 || backCluster < 0 || frontCluster == backCluster)
						continue;

					var points = portal.Winding.WithoutColinearPoints();
					if (points.Count < 3)
						continue;

					foreach (var piece in SplitForVvis(points))
					{
						// vvis takes the portal's plane from its first three points
						var normal = Winding.PlaneNormal(piece[0], piece[1], piece[2]);
						result.Add(Vector3d.Dot(normal, portal.Plane.Normal) >= 0 ?
							new ClusterPortal(frontCluster, backCluster, piece) :
							new ClusterPortal(backCluster, frontCluster, piece));
					}
				}
			}

			return result;
		}

		// The clusters of the leaves with a portal to the outside
		private HashSet<int> GetOutsideClusters(IReadOnlyList<int> leafClusters)
		{
			var result = new HashSet<int>();
			for (var id = nodes.Count; id < nodePortals.Length; id++)
			{
				var cluster = leafClusters[id - nodes.Count];
				if (cluster >= 0 && nodePortals[id].Exists(portal => portal.Front == Outside || portal.Back == Outside))
					result.Add(cluster);
			}

			return result;
		}

		// Starts the polygon at the corner whose two edges are furthest from parallel, since vvis takes the plane
		// from the first three points, and cuts it into fans of at most MaxPortalPoints points
		private static IEnumerable<Vector3d[]> SplitForVvis(List<Vector3d> points)
		{
			var count = points.Count;
			var best = 0;
			var bestArea = -1.0;
			for (var i = 0; i < count; i++)
			{
				var area = Winding.PlaneNormalUnnormalized(points[i], points[(i + 1) % count], points[(i + 2) % count]).Length;
				if (area > bestArea)
				{
					bestArea = area;
					best = i;
				}
			}

			var rotated = new Vector3d[count];
			for (var i = 0; i < count; i++)
				rotated[i] = points[(best + i) % count];

			if (count <= MaxPortalPoints)
			{
				yield return rotated;
				yield break;
			}

			for (var start = 1; start < count - 1; start += MaxPortalPoints - 2)
			{
				var end = Math.Min(start + MaxPortalPoints - 2, count - 1);
				var piece = new Vector3d[end - start + 2];
				piece[0] = rotated[0];
				Array.Copy(rotated, start, piece, 1, end - start + 1);
				yield return piece;
			}
		}

		// Writes the portals as a PRT1 file for vvis
		public static void WritePortalFile(string path, int numClusters, IReadOnlyList<ClusterPortal> portals)
		{
			var text = new StringBuilder();
			text.Append("PRT1\n");
			text.Append(numClusters).Append('\n');
			text.Append(portals.Count).Append('\n');
			foreach (var portal in portals)
			{
				text.Append(portal.Points.Length).Append(' ').Append(portal.FrontCluster).Append(' ').Append(portal.BackCluster).Append(' ');
				foreach (var point in portal.Points)
				{
					text.Append('(');
					for (var i = 0; i < 3; i++)
						text.Append(point[i].ToString("0.######", CultureInfo.InvariantCulture)).Append(' ');
					text.Append(") ");
				}
				text.Append('\n');
			}

			File.WriteAllText(path, text.ToString());
		}
	}
}
