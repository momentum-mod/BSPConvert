using LibBSP;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace BSPConvert.Lib.Zones
{
	public class ZoneGenerator
	{
		private readonly ILogger logger;

		private readonly Entities entities;
		private readonly Lump<Model> models;
		private readonly Lump<Brush> brushes;
		private readonly Lump<BrushSide> brushSides;
		private readonly Lump<PlaneBSP> planes;

		private const float VerticalEpsilon = 0.01f;

		public ZoneGenerator(BSP sourceBsp, BSP quakeBsp, ILogger logger)
		{
			this.logger = logger;

			this.entities = sourceBsp.Entities;

			// Note: Use the Quake 3 BSP for zone geometry since it's easier to extract region polygons from brush models
			this.models = quakeBsp.Models;
			this.brushes = quakeBsp.Brushes;
			this.brushSides = quakeBsp.BrushSides;
			this.planes = quakeBsp.Planes;
		}

		public ZoneDefsBase Generate()
		{
			var startEntities = GetEntitiesByClassName("zone_timer_start");
			var endEntities = GetEntitiesByClassName("zone_timer_end");
			var checkpointEntities = GetEntitiesByClassName("zone_timer_checkpoint");

			var zoneDefs = new ZoneDefsBase
			{
				FormatVersion = 1,
				DataTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
			};

			var mainTrack = new MainTrack();
			mainTrack.StagesEndAtStageStarts = true;

			var segment = new Segment
			{
				LimitStartGroundSpeed = false,
				CheckpointsRequired = false,
				CheckpointsOrdered = false
			};

			// Start zone -> one region per zone_timer_start entity
			if (startEntities.Count > 0)
			{
				var startZone = new Zone();

				foreach (var startEntity in startEntities)
				{
					var region = CreateRegionFromModel(startEntity);
					if (region == null)
					{
						logger.Log("Warning: Failed to create start zone region.");
						continue;
					}

					var restartDest = startEntity["restart_destination"];
					region.TeleDestTargetname = restartDest;
					region.SafeHeight = -1; // Full height

					startZone.Regions.Add(region);
				}

				if (startZone.Regions.Count > 0)
					segment.Checkpoints.Add(startZone);
			}

			// Checkpoints -> subsequent checkpoints in the same segment (sorted by checkpoint number)
			var sortedCheckpoints = checkpointEntities
				.OrderBy(e => ParseInt(e["checkpoint_number"], 0))
				.ToList();

			foreach (var checkpoint in sortedCheckpoints)
			{
				var zone = CreateZone(checkpoint);
				if (zone.Regions.Count == 0)
				{
					logger.Log($"Warning: Failed to create checkpoint zone region for checkpoint {checkpoint["checkpoint_number"]}. Skipping checkpoint.");
					continue;
				}

				segment.Checkpoints.Add(zone);
			}

			mainTrack.Zones.Segments.Add(segment);

			// End zone -> one region per zone_timer_end entity
			if (endEntities.Count > 0)
			{
				var endZone = new Zone();

				foreach (var endEntity in endEntities)
				{
					var region = CreateRegionFromModel(endEntity);
					if (region == null)
					{
						logger.Log("Warning: Failed to create end zone region.");
						continue;
					}

					endZone.Regions.Add(region);
				}

				if (endZone.Regions.Count > 0)
					mainTrack.Zones.End = endZone;
			}

			zoneDefs.Tracks.Main = mainTrack;

			return zoneDefs;
		}

		private Zone CreateZone(Entity entity)
		{
			var zone = new Zone();
			var region = CreateRegionFromModel(entity);

			if (region != null)
				zone.Regions.Add(region);

			return zone;
		}

		private Region? CreateRegionFromModel(Entity entity)
		{
			var modelNumber = entity.ModelNumber;
			if (modelNumber < 0 || modelNumber >= models.Count)
			{
				logger.Log($"Warning: Entity with classname '{entity.ClassName}' has invalid model number {modelNumber}. Skipping region generation for this entity.");
				return null;
			}

			var model = models[modelNumber];
			var mins = model.Minimums;
			var maxs = model.Maximums;

			var bottom = mins.Z();
			var height = maxs.Z() - mins.Z();

			var points = ComputeBrushPolygon(model);
			if (points == null || points.Count < 3)
			{
				// Fallback to AABB if brush planes can't produce a valid polygon
				points = new List<float[]>
				{
					new[] { mins.X(), maxs.Y() },
					new[] { mins.X(), mins.Y() },
					new[] { maxs.X(), mins.Y() },
					new[] { maxs.X(), maxs.Y() }
				};
			}

			return new Region
			{
				Points = points,
				Bottom = bottom,
				Height = height
			};
		}

		/// <summary>
		/// Computes the 2D polygon for a brush model by intersecting the vertical brush side half-spaces.
		/// Starts with the model's AABB as an initial polygon, then clips it against each vertical plane.
		/// </summary>
		private List<float[]>? ComputeBrushPolygon(Model model)
		{
			var mins = model.Minimums;
			var maxs = model.Maximums;

			// Start with the AABB as the initial polygon (counter-clockwise)
			var polygon = new List<Vector2>
			{
				new Vector2(mins.X(), maxs.Y()),
				new Vector2(mins.X(), mins.Y()),
				new Vector2(maxs.X(), mins.Y()),
				new Vector2(maxs.X(), maxs.Y())
			};

			// Gather all vertical planes from the model's brushes
			var verticalPlanes = GetVerticalPlanes(model);

			// Clip the polygon against each vertical plane's half-space
			foreach (var plane in verticalPlanes)
			{
				var normal2D = new Vector2(plane.Normal.X(), plane.Normal.Y());
				var dist = plane.Distance;

				polygon = ClipPolygonByPlane(polygon, normal2D, dist);
				if (polygon.Count < 3)
					return null;
			}

			return polygon.Select(p => new[] { p.X, p.Y }).ToList();
		}

		/// <summary>
		/// Collects all vertical brush side planes from the brushes belonging to the specified model.
		/// A plane is vertical if its normal has no significant Z component.
		/// </summary>
		private List<PlaneBSP> GetVerticalPlanes(Model model)
		{
			var verticalPlanes = new List<PlaneBSP>();
			var brush = brushes[model.FirstBrushIndex];

			for (var i = 0; i < brush.NumSides; i++)
			{
				var side = brushSides[brush.FirstSideIndex + i];
				var plane = planes[side.PlaneIndex];
				var normal = plane.Normal;

				// A vertical plane has a normal with negligible Z component
				if (Math.Abs(normal.Z()) < VerticalEpsilon)
					verticalPlanes.Add(plane);
			}

			return verticalPlanes;
		}

		/// <summary>
		/// Clips a 2D convex polygon by a half-space defined by normal · point <= dist.
		/// Uses the Sutherland-Hodgman algorithm.
		/// </summary>
		private static List<Vector2> ClipPolygonByPlane(List<Vector2> polygon, Vector2 normal, float dist)
		{
			var output = new List<Vector2>();

			for (var i = 0; i < polygon.Count; i++)
			{
				var current = polygon[i];
				var next = polygon[(i + 1) % polygon.Count];

				var currentDist = Vector2.Dot(normal, current) - dist;
				var nextDist = Vector2.Dot(normal, next) - dist;

				var currentInside = currentDist <= VerticalEpsilon;
				var nextInside = nextDist <= VerticalEpsilon;

				if (currentInside)
					output.Add(current);

				// If the edge crosses the plane, compute the intersection
				if (currentInside != nextInside)
				{
					var t = currentDist / (currentDist - nextDist);
					var intersection = current + t * (next - current);
					output.Add(intersection);
				}
			}

			return output;
		}

		private List<Entity> GetEntitiesByClassName(string className)
		{
			return entities
				.Where(e => e.ClassName == className)
				.ToList();
		}

		private static int ParseInt(string value, int defaultValue)
		{
			if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
				return result;

			return defaultValue;
		}
	}
}
