using BSPConvert.Lib.Vis;

namespace BSPConvert.Test
{
	public class TreePortalsTests
	{
		private static readonly Vector3d Mins = new Vector3d(0, 0, 0);
		private static readonly Vector3d Maxs = new Vector3d(100, 100, 100);

		private static TreePortals.TreePlane Plane(double x, double y, double z, double distance) =>
			new TreePortals.TreePlane(new Vector3d(x, y, z), distance);

		private static int Leaf(int leaf) => -(leaf + 1);

		private static List<ClusterPortal> Build(TreePortals.TreePlane[] planes, TreePortals.TreeNode[] nodes, int[] leafClusters) =>
			TreePortals.Build(planes, nodes, 0, leafClusters, Mins, Maxs).Portals;

		[Test]
		public void SplitBoxHasOnePortalFacingItsFrontCluster()
		{
			var planes = new[] { Plane(1, 0, 0, 50) };
			var nodes = new[] { new TreePortals.TreeNode(0, Leaf(1), Leaf(0)) };

			var portals = Build(planes, nodes, new[] { 0, 1 });

			Assert.That(portals, Has.Count.EqualTo(1));
			var portal = portals[0];
			Assert.That(portal.Points.All(point => Math.Abs(point.X - 50) < 1e-6));
			var normal = Winding.PlaneNormal(portal.Points[0], portal.Points[1], portal.Points[2]);
			Assert.That(normal.X > 0 ? (portal.FrontCluster, portal.BackCluster) : (portal.BackCluster, portal.FrontCluster), Is.EqualTo((1, 0)));
		}

		[Test]
		public void SolidLeavesAndSharedClustersHaveNoPortals()
		{
			// x < 50 and x > 60 are separated by a solid slab
			var planes = new[] { Plane(1, 0, 0, 50), Plane(1, 0, 0, 60) };
			var nodes = new[]
			{
				new TreePortals.TreeNode(0, 1, Leaf(0)),
				new TreePortals.TreeNode(1, Leaf(2), Leaf(1)),
			};

			Assert.That(Build(planes, nodes, new[] { 0, -1, 1 }), Is.Empty);
			Assert.That(Build(new[] { planes[0] }, new[] { new TreePortals.TreeNode(0, Leaf(1), Leaf(0)) }, new[] { 3, 3 }), Is.Empty);
		}

		[Test]
		public void VoidIsWhatNoEntityCanSee()
		{
			// x < 50 and x > 60 are separated by a solid slab, and x > 60 is split in two by y = 50
			var planes = new[] { Plane(1, 0, 0, 50), Plane(1, 0, 0, 60), Plane(0, 1, 0, 50) };
			var nodes = new[]
			{
				new TreePortals.TreeNode(0, 1, Leaf(0)),
				new TreePortals.TreeNode(1, 2, Leaf(1)),
				new TreePortals.TreeNode(2, Leaf(3), Leaf(2)),
			};

			var portals = TreePortals.Build(planes, nodes, 0, new[] { 0, -1, 1, 2 }, Mins, Maxs);

			Assert.That(portals.OutsideClusters, Is.EquivalentTo(new[] { 0, 1, 2 }));
			Assert.That(portals.FindVoid(new HashSet<int> { 0 }), Is.EquivalentTo(new[] { 1, 2 }));
			Assert.That(portals.FindVoid(new HashSet<int> { 2 }), Is.EquivalentTo(new[] { 0 }));
			Assert.That(portals.FindVoid(new HashSet<int> { 0, 1 }), Is.Empty);
			Assert.That(TreePortals.FindLeaf(planes, nodes, 0, new Vector3d(70, 80, 10)), Is.EqualTo(3));
		}

		[Test]
		public void PortalsAreSplitBetweenTheLeavesTheyBound()
		{
			// x < 50 is one leaf, x > 50 is split into y < 50 and y > 50
			var planes = new[] { Plane(1, 0, 0, 50), Plane(0, 1, 0, 50) };
			var nodes = new[]
			{
				new TreePortals.TreeNode(0, 1, Leaf(0)),
				new TreePortals.TreeNode(1, Leaf(2), Leaf(1)),
			};

			var portals = Build(planes, nodes, new[] { 0, 1, 2 });

			var pairs = portals.Select(portal => (Math.Min(portal.FrontCluster, portal.BackCluster), Math.Max(portal.FrontCluster, portal.BackCluster))).ToList();
			Assert.That(pairs, Is.EquivalentTo(new[] { (0, 1), (0, 2), (1, 2) }));

			var between1And2 = portals.Single(portal => portal.FrontCluster + portal.BackCluster == 3);
			Assert.That(between1And2.Points.All(point => Math.Abs(point.Y - 50) < 1e-6 && point.X >= 50 - 1e-6));
			var between0And1 = portals.Single(portal => portal.FrontCluster + portal.BackCluster == 1);
			Assert.That(between0And1.Points.All(point => Math.Abs(point.X - 50) < 1e-6 && point.Y <= 50 + 1e-6));
		}
	}
}
