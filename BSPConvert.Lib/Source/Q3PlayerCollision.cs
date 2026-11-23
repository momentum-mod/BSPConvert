using LibBSP;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace BSPConvert.Lib
{
	// Places the standing Quake 3 player box against the world brushes, the way bg_pmove.c moves it
	public class Q3PlayerCollision
	{
		// The standing player box from its feet (bg_pmove.c PM_CheckDuck: mins -15 -15 -24, maxs 15 15 32)
		private const double HalfWidth = 15.0;
		private const double Height = 56.0;

		// bg_local.h STEPSIZE
		private const double StepSize = 18.0;

		// How far the box can reach behind a brush plane and still count as outside it, for float error in the planes
		private const double PlaneEpsilon = 0.01;

		private const int MaxLiftIterations = 8;

		private const Q3ContentsFlags PlayerSolid = Q3ContentsFlags.CONTENTS_SOLID | Q3ContentsFlags.CONTENTS_PLAYERCLIP;

		private readonly record struct Side(Vector3D Normal, double Distance);
		private readonly record struct Vector3D(double X, double Y, double Z);
		private readonly record struct SolidBrush(Side[] Sides, Vector3D Mins, Vector3D Maxs);

		private readonly List<SolidBrush> brushes = new List<SolidBrush>();

		public Q3PlayerCollision(BSP q3Bsp)
		{
			var world = q3Bsp.Models[0];
			for (var i = world.FirstBrushIndex; i < world.FirstBrushIndex + world.NumBrushes; i++)
			{
				var brush = q3Bsp.Brushes[i];
				if (((Q3ContentsFlags)brush.Texture.Contents & PlayerSolid) == 0)
					continue;

				var sides = new Side[brush.NumSides];
				for (var j = 0; j < brush.NumSides; j++)
				{
					var plane = q3Bsp.BrushSides[brush.FirstSideIndex + j].Plane;
					sides[j] = new Side(new Vector3D(plane.Normal.X, plane.Normal.Y, plane.Normal.Z), plane.Distance);
				}

				brushes.Add(new SolidBrush(sides, GetAxialBound(sides, -1.0), GetAxialBound(sides, 1.0)));
			}
		}

		public enum Placement
		{
			Clear,
			SteppedUp,
			Stuck,
		}

		/// <summary>
		/// Where Quake 3 movement puts a player whose feet start at the given position. Its traces can leave a brush
		/// they start in, so a player up to a step into the floor steps up onto it.
		/// </summary>
		public Placement Place(Vector3 feet, out Vector3 placed)
		{
			placed = feet;

			var lift = 0.0;
			for (var iteration = 0; iteration < MaxLiftIterations; iteration++)
			{
				var center = new Vector3D(feet.X, feet.Y, feet.Z + lift + Height / 2.0);
				var needed = 0.0;
				var overlapping = false;

				foreach (var brush in brushes)
				{
					if (!Overlaps(brush, center))
						continue;

					overlapping = true;
					var brushLift = GetLiftOutOf(brush, center);
					if (brushLift == null)
						return Placement.Stuck;

					needed = Math.Max(needed, brushLift.Value);
				}

				if (!overlapping)
				{
					if (lift == 0.0)
						return Placement.Clear;

					placed = feet with { Z = feet.Z + (float)lift + 1f }; // 1 unit clear, like TeleportPlayer
					return Placement.SteppedUp;
				}

				lift += needed;
				if (lift > StepSize)
					return Placement.Stuck;
			}

			return Placement.Stuck;
		}

		private static bool Overlaps(SolidBrush brush, Vector3D center)
		{
			if (center.X + HalfWidth <= brush.Mins.X + PlaneEpsilon || center.X - HalfWidth >= brush.Maxs.X - PlaneEpsilon ||
				center.Y + HalfWidth <= brush.Mins.Y + PlaneEpsilon || center.Y - HalfWidth >= brush.Maxs.Y - PlaneEpsilon ||
				center.Z + Height / 2.0 <= brush.Mins.Z + PlaneEpsilon || center.Z - Height / 2.0 >= brush.Maxs.Z - PlaneEpsilon)
				return false;

			foreach (var side in brush.Sides)
			{
				if (GetBoxDistance(side, center) > -PlaneEpsilon)
					return false;
			}

			return true;
		}

		// The smallest upward move that brings the box onto one of the brush's upward-facing planes
		private static double? GetLiftOutOf(SolidBrush brush, Vector3D center)
		{
			double? lift = null;
			foreach (var side in brush.Sides)
			{
				if (side.Normal.Z <= 1e-6)
					continue;

				var sideLift = -GetBoxDistance(side, center) / side.Normal.Z;
				if (lift == null || sideLift < lift)
					lift = sideLift;
			}

			return lift;
		}

		// How far the box's nearest corner is in front of the plane (negative when the box reaches behind it)
		private static double GetBoxDistance(Side side, Vector3D center)
		{
			var n = side.Normal;
			var extents = Math.Abs(n.X) * HalfWidth + Math.Abs(n.Y) * HalfWidth + Math.Abs(n.Z) * Height / 2.0;
			return n.X * center.X + n.Y * center.Y + n.Z * center.Z - extents - side.Distance;
		}

		// The brush's bounds along each axis from its axial planes, or unbounded where it has none
		private static Vector3D GetAxialBound(Side[] sides, double sign)
		{
			double x = sign * double.PositiveInfinity, y = x, z = x;
			foreach (var side in sides)
			{
				var n = side.Normal;
				if (n.X * sign > 0.9999)
					x = side.Distance * sign;
				else if (n.Y * sign > 0.9999)
					y = side.Distance * sign;
				else if (n.Z * sign > 0.9999)
					z = side.Distance * sign;
			}

			return new Vector3D(x, y, z);
		}
	}
}
