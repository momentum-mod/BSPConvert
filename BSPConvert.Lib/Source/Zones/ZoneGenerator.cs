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

		private readonly SourceBspBuilder output;
		private readonly Entities entities;
		private readonly Lump<Model> models;
		private readonly Lump<Brush> brushes;
		private readonly Lump<BrushSide> brushSides;
		private readonly Lump<PlaneBSP> planes;

		// How far outside a brush plane a corner can be and still count as part of the brush
		private const double PlaneEpsilon = 0.05;

		// Momentum rejects regions that aren't taller than 1 unit
		private const double MinRegionHeight = 2.0;

		// Merged regions may only add this much area (in square units, or as a fraction of the merged area)
		private const double MergeAreaTolerance = 1.0;
		private const double MergeAreaFraction = 0.001;

		// A zone volume: an extruded 2D polygon (counter-clockwise), the way Momentum stores regions
		private record Volume(List<Point2> Footprint, double Bottom, double Top);

		// Reads zone entities and their brush geometry from the converted Source BSP, so zones work for any input
		// engine whose converter records its models' brushes (see SourceBspBuilder.SetModelBrushes).
		public ZoneGenerator(SourceBspBuilder output, ILogger logger)
		{
			this.logger = logger;
			this.output = output;

			var sourceBsp = output.Bsp;
			this.entities = sourceBsp.Entities;
			this.models = sourceBsp.Models;
			this.brushes = sourceBsp.Brushes;
			this.brushSides = sourceBsp.BrushSides;
			this.planes = sourceBsp.Planes;
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

			// Start zone -> the regions of every zone_timer_start entity
			if (startEntities.Count > 0)
			{
				var startZone = CreateZone(startEntities, "start zone");
				foreach (var region in startZone.Regions)
				{
					region.TeleDestTargetname = startEntities[0]["restart_destination"];
					region.SafeHeight = -1; // Full height
				}

				if (startZone.Regions.Count > 0)
					segment.Checkpoints.Add(startZone);
			}

			// Checkpoints -> subsequent checkpoints in the same segment (sorted by checkpoint number). Triggers that
			// fire the same Defrag checkpoint share a number, so they become one zone with several regions.
			var checkpointGroups = checkpointEntities
				.GroupBy(e => ParseInt(e["checkpoint_number"], 0))
				.OrderBy(g => g.Key);

			foreach (var checkpointGroup in checkpointGroups)
			{
				var zone = CreateZone(checkpointGroup.ToList(), $"checkpoint {checkpointGroup.Key}");
				if (zone.Regions.Count == 0)
				{
					logger.Log($"Warning: Couldn't create the regions of checkpoint {checkpointGroup.Key}, so it's left out");
					continue;
				}

				segment.Checkpoints.Add(zone);
			}

			mainTrack.Zones.Segments.Add(segment);

			// End zone -> the regions of every zone_timer_end entity
			if (endEntities.Count > 0)
			{
				var endZone = CreateZone(endEntities, "end zone");
				if (endZone.Regions.Count > 0)
					mainTrack.Zones.End = endZone;
			}

			zoneDefs.Tracks.Main = mainTrack;

			return zoneDefs;
		}

		private Zone CreateZone(List<Entity> zoneEntities, string zoneName)
		{
			var volumes = new List<Volume>();
			foreach (var entity in zoneEntities)
				volumes.AddRange(CreateEntityVolumes(entity, zoneName));

			var zone = new Zone();
			foreach (var volume in MergeVolumes(volumes))
				zone.Regions.Add(CreateRegion(volume));

			return zone;
		}

		// One volume per brush: a Quake trigger touches the player if any of its brushes do, and a model's brushes
		// can be far apart (or form a non-convex shape together), so each one becomes its own region
		private List<Volume> CreateEntityVolumes(Entity entity, string zoneName)
		{
			var volumes = new List<Volume>();

			var modelNumber = entity.ModelNumber;
			if (modelNumber < 0 || modelNumber >= models.Count)
			{
				logger.Log($"Warning: {entity.ClassName} has an invalid model number {modelNumber}, so it has no zone region");
				return volumes;
			}

			// Brush models are built relative to their entity's origin
			var origin = entity.ContainsKey("origin") ? entity.Origin : new Vector3(0f, 0f, 0f);

			foreach (var brushIndex in output.GetModelBrushes(modelNumber))
			{
				var volume = CreateBrushVolume(brushes[brushIndex], origin, zoneName);
				if (volume != null)
					volumes.Add(volume);
			}

			if (volumes.Count == 0)
			{
				logger.Log($"Warning: Couldn't read the brushes of the {zoneName}'s model *{modelNumber}, so its region uses the model's bounds");

				var model = models[modelNumber];
				var corners = new[]
				{
					new Point2(model.Minimums.X() + origin.X(), model.Minimums.Y() + origin.Y()),
					new Point2(model.Maximums.X() + origin.X(), model.Maximums.Y() + origin.Y())
				};
				volumes.Add(new Volume(RegionPolygon.BoundingRectangle(corners), model.Minimums.Z() + origin.Z(), model.Maximums.Z() + origin.Z()));
			}

			return volumes;
		}

		/// <summary>
		/// The brush's footprint (the convex hull of its corners seen from above) extruded over its height. This is
		/// exact for brushes whose sides are vertical apart from the top and bottom, and covers sloped brushes.
		/// </summary>
		private Volume? CreateBrushVolume(Brush brush, Vector3 origin, string zoneName)
		{
			var corners = GetBrushCorners(brush, includeBevels: false);
			if (corners.Count < 4)
				corners = GetBrushCorners(brush, includeBevels: true); // In case a real side was taken for a bevel

			if (corners.Count < 4)
			{
				logger.Log($"Warning: A brush of the {zoneName} has no volume, so it's left out");
				return null;
			}

			var bottom = corners.Min(c => c.Z) + origin.Z();
			var top = corners.Max(c => c.Z) + origin.Z();

			var projected = corners.Select(c => new Point2(c.X + origin.X(), c.Y + origin.Y())).ToList();
			var footprint = RegionPolygon.ConvexHull(projected);
			var simplified = footprint.Count >= 3 ? RegionPolygon.Simplify(footprint) : null;
			if (simplified == null)
			{
				logger.Log($"Warning: A brush of the {zoneName} is too thin or too sharp for a zone region, so its region uses the brush's bounds");
				simplified = RegionPolygon.BoundingRectangle(projected);
			}

			return new Volume(simplified, bottom, top);
		}

		/// <summary>
		/// The brush's corners: the points where three of its planes meet that are inside every other plane.
		/// Bevel planes are skipped; they only touch the brush, but a slightly off bevel can shave its corners.
		/// </summary>
		private List<(double X, double Y, double Z)> GetBrushCorners(Brush brush, bool includeBevels)
		{
			var sidePlanes = new List<(double X, double Y, double Z, double D)>();
			for (var i = 0; i < brush.NumSides; i++)
			{
				var side = brushSides[brush.FirstSideIndex + i];
				if (side.IsBevel && !includeBevels)
					continue;

				var plane = planes[side.PlaneIndex];
				sidePlanes.Add((plane.Normal.X(), plane.Normal.Y(), plane.Normal.Z(), plane.Distance));
			}

			var corners = new List<(double X, double Y, double Z)>();
			for (var i = 0; i < sidePlanes.Count; i++)
			{
				for (var j = i + 1; j < sidePlanes.Count; j++)
				{
					for (var k = j + 1; k < sidePlanes.Count; k++)
					{
						if (!TryIntersectPlanes(sidePlanes[i], sidePlanes[j], sidePlanes[k], out var point))
							continue;

						var inside = sidePlanes.All(p => p.X * point.X + p.Y * point.Y + p.Z * point.Z - p.D <= PlaneEpsilon);
						if (inside)
							corners.Add(point);
					}
				}
			}

			return corners;
		}

		// Cramer's rule in doubles: Quake maps reach ±32768 units, where floats lose the precision needed here
		private static bool TryIntersectPlanes((double X, double Y, double Z, double D) a, (double X, double Y, double Z, double D) b,
			(double X, double Y, double Z, double D) c, out (double X, double Y, double Z) point)
		{
			var bc = Cross(b, c);
			var denom = a.X * bc.X + a.Y * bc.Y + a.Z * bc.Z;
			if (Math.Abs(denom) < 1e-9)
			{
				point = default;
				return false;
			}

			var ca = Cross(c, a);
			var ab = Cross(a, b);
			point = (
				(bc.X * a.D + ca.X * b.D + ab.X * c.D) / denom,
				(bc.Y * a.D + ca.Y * b.D + ab.Y * c.D) / denom,
				(bc.Z * a.D + ca.Z * b.D + ab.Z * c.D) / denom);
			return true;

			static (double X, double Y, double Z) Cross((double X, double Y, double Z, double D) u, (double X, double Y, double Z, double D) v)
			{
				return (u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
			}
		}

		/// <summary>
		/// Merges volumes that span the same heights and whose union is convex (e.g. a trigger built from adjacent
		/// boxes, or the same trigger referenced twice), so zones don't carry more regions than they need.
		/// </summary>
		private static List<Volume> MergeVolumes(List<Volume> volumes)
		{
			var merged = new List<Volume>(volumes);

			for (var i = 0; i < merged.Count; i++)
			{
				for (var j = i + 1; j < merged.Count; j++)
				{
					if (!TryMergeVolumes(merged[i], merged[j], out var union))
						continue;

					merged[i] = union;
					merged.RemoveAt(j);
					j = i; // The grown volume may now merge with ones already checked
				}
			}

			return merged;
		}

		private static bool TryMergeVolumes(Volume a, Volume b, out Volume union)
		{
			union = a;

			if (Math.Abs(a.Bottom - b.Bottom) > PlaneEpsilon || Math.Abs(a.Top - b.Top) > PlaneEpsilon)
				return false;

			var hull = RegionPolygon.ConvexHull(a.Footprint.Concat(b.Footprint));
			if (hull.Count < 3)
				return false;

			var overlap = RegionPolygon.Intersect(a.Footprint, b.Footprint);
			var overlapArea = overlap.Count >= 3 ? RegionPolygon.Area(overlap) : 0.0;
			var unionArea = RegionPolygon.Area(a.Footprint) + RegionPolygon.Area(b.Footprint) - overlapArea;
			var hullArea = RegionPolygon.Area(hull);

			// The union is convex when its hull adds (next to) nothing
			if (hullArea - unionArea > Math.Max(MergeAreaTolerance, hullArea * MergeAreaFraction))
				return false;

			var simplified = RegionPolygon.Simplify(hull);
			if (simplified == null)
				return false;

			union = new Volume(simplified, Math.Min(a.Bottom, b.Bottom), Math.Max(a.Top, b.Top));
			return true;
		}

		private Region CreateRegion(Volume volume)
		{
			var height = volume.Top - volume.Bottom;
			if (height < MinRegionHeight)
			{
				logger.Log($"Warning: A zone region is only {height.ToString("0.##", CultureInfo.InvariantCulture)} units tall, so it's raised to {MinRegionHeight} units");
				height = MinRegionHeight;
			}

			return new Region
			{
				Points = volume.Footprint.Select(p => new[] { (float)p.X, (float)p.Y }).ToList(),
				Bottom = (float)volume.Bottom,
				Height = (float)height
			};
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
