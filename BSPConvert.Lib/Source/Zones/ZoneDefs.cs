using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BSPConvert.Lib.Zones
{
	public class ZoneDefsBase
	{
		[JsonPropertyName("formatVersion")]
		public int FormatVersion { get; set; }

		[JsonPropertyName("dataTimestamp")]
		public long DataTimestamp { get; set; }

		[JsonPropertyName("maxVelocity")]
		public float? MaxVelocity { get; set; }

		[JsonPropertyName("tracks")]
		public MapTracks Tracks { get; set; } = new();

		[JsonPropertyName("globalRegions")]
		public GlobalRegions? GlobalRegions { get; set; }
	}

	public class MapTracks
	{
		[JsonPropertyName("main")]
		public MainTrack? Main { get; set; }

		[JsonPropertyName("bonuses")]
		public List<BonusTrack> Bonuses { get; set; } = new();
	}

	public class MainTrack
	{
		[JsonPropertyName("zones")]
		public TrackZones Zones { get; set; } = new();

		[JsonPropertyName("stagesEndAtStageStarts")]
		public bool StagesEndAtStageStarts { get; set; } = true;

		[JsonPropertyName("bhopEnabled")]
		public bool? BhopEnabled { get; set; }
	}

	public class BonusTrack
	{
		[JsonPropertyName("zones")]
		public TrackZones? Zones { get; set; }

		[JsonPropertyName("defragModifiers")]
		public byte? DefragModifiers { get; set; }

		[JsonPropertyName("bhopEnabled")]
		public bool? BhopEnabled { get; set; }
	}

	public class TrackZones
	{
		[JsonPropertyName("segments")]
		public List<Segment> Segments { get; set; } = new();

		[JsonPropertyName("end")]
		public Zone End { get; set; } = new();
	}

	public class Segment
	{
		[JsonPropertyName("limitStartGroundSpeed")]
		public bool LimitStartGroundSpeed { get; set; }

		[JsonPropertyName("checkpointsRequired")]
		public bool CheckpointsRequired { get; set; } = true;

		[JsonPropertyName("checkpointsOrdered")]
		public bool CheckpointsOrdered { get; set; } = true;

		[JsonPropertyName("checkpoints")]
		public List<Zone> Checkpoints { get; set; } = new();

		[JsonPropertyName("cancel")]
		public List<Zone> Cancel { get; set; } = new();

		[JsonPropertyName("name")]
		public string? Name { get; set; }
	}

	public class Zone
	{
		[JsonPropertyName("regions")]
		public List<Region> Regions { get; set; } = new();

		[JsonPropertyName("filtername")]
		public string? Filtername { get; set; }
	}

	public class Region
	{
		[JsonPropertyName("points")]
		public List<float[]> Points { get; set; } = new();

		[JsonPropertyName("bottom")]
		public float? Bottom { get; set; }

		[JsonPropertyName("height")]
		public float Height { get; set; } = 256.0f;

		[JsonPropertyName("teleDestTargetname")]
		public string? TeleDestTargetname { get; set; }

		[JsonPropertyName("teleDestPos")]
		public float[]? TeleDestPos { get; set; }

		[JsonPropertyName("teleDestYaw")]
		public float? TeleDestYaw { get; set; }

		[JsonPropertyName("safeHeight")]
		public float? SafeHeight { get; set; }
	}

	public class GlobalRegions
	{
		[JsonPropertyName("allowBhop")]
		public List<Region>? AllowBhop { get; set; }

		[JsonPropertyName("overbounce")]
		public List<Region>? Overbounce { get; set; }

		[JsonPropertyName("cancel")]
		public List<Region>? Cancel { get; set; }
	}
}
