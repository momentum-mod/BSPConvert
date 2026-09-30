using LibBSP;
using System;
using System.IO;
using System.Linq;
using BSPConvert.Lib.Zones;

namespace BSPConvert.Lib
{
	public class BSPConverterOptions
	{
		public bool noPak;
		public bool noToolDisplacements;
		public bool patchesAsPrimitives;
		private int displacementPower;
		public int DisplacementPower
		{
			get { return displacementPower; }
			set { displacementPower = Math.Clamp(value, 2, 4); }
		}
		public int minDamageToRespawnPlayer;
		// Duplicate Quake 3 lava brushes (CONTENTS_LAVA) into trigger_hurt volumes so players are
		// killed/respawned on contact. See Q3Converter.ConvertLavaTriggers.
		public bool lavaTriggers;
		// Quake 3 fog brushes are converted by default: the default path (useObbFog = false) tags each fog brush
		// CONTENTS_FOG and drops its faces (nodraw). The engine composites a depth-clipped fog overlay for the
		// volume, reading the appearance from the brush's Fog material. See Q3Converter.ConvertPolygon / ConvertBrushes.
		// Use the legacy obb_volumefog entity path instead of the Fog shader. obb_volumefog is a froxel
		// volumetric that handles arbitrary brush shapes but flickers on thin volumes; kept behind this
		// flag for now. See Q3Converter.ConvertObbFog.
		public bool useObbFog;
		// Minimum vertical (Z) height, in units, for converted fog volumes. Thin fog layers are expanded
		// downward to this height so they span enough view froxels to reduce flickering. obb-fog only.
		// See ConvertObbFog.
		public float fogMinHeight;
		// Skip generating the fog overlay face for fog shaders with visible stages (e.g. the scrolling
		// clouds on textures/sfx/hellfog); the fog brush face is just dropped instead. See TryCreateFogOverlayFace.
		public bool noFogOverlay;
		public bool ignoreZones;
		public bool noEnvMap;
		public bool oldBSP;
		// LZMA compress the BSP's lumps
		public bool compress;
		public string prefix;
		public string inputFile;
		public string outputDir;
		// Optional filter to convert only specific BSP(s) from a multi-BSP pk3. Matched against each
		// BSP's map name (without extension), case-insensitively. Null/empty converts every BSP.
		public string[] mapFilter;
		public string offModeEntityFallback;
		// When set, applies Quake 3's hue-preserving overbright clamp to lightmap luxels (flattens
		// over-bright highlights toward white). Off by default. See ColorUtil.ConvertQ3LightmapToColorRGBExp32.
		public bool clampOverbright;
		// Uniform multiplier applied to the Quake 3 map's geometry (vertices, plane distances, bounding
		// boxes) and position-based entity data before conversion, so the converted map is bigger/smaller
		// than the original. 1 (default) makes no change. See Q3Converter.ScaleQuakeBsp.
		public float scale = 1f;
		// Settings for baking Q3 multi-pass scrolling shaders (e.g. liquids water) into looping animated
		// flipbook VTFs. See FlipbookConverter.
		public FlipbookOptions flipbook = new FlipbookOptions();
	}

	// Entry point for a conversion: loads the input file's BSP(s), hands each one to the converter for its
	// engine (see IEngineConverter), then writes the resulting Source BSP and its zone file.
	public class BSPConverter
	{
		private BSPConverterOptions options;
		private ILogger logger;

		private ContentManager contentManager;
		private Q3Converter? q3Converter;

		public BSPConverter(BSPConverterOptions options, ILogger logger)
		{
			this.options = options;
			this.logger = logger;
		}

		public void Convert()
		{
			if (!File.Exists(options.inputFile))
			{
				logger.Log("Error: Input BSP file does not exist");
				return;
			}

			contentManager = new ContentManager(options.inputFile);

			foreach (var bsp in contentManager.BSPFiles)
			{
				if (options.mapFilter != null && options.mapFilter.Length > 0 &&
					!options.mapFilter.Contains(bsp.MapName, StringComparer.OrdinalIgnoreCase))
				{
					logger.Log($"Skipping {bsp.MapName}.bsp (not in --maps filter)");
					continue;
				}

				var engineConverter = GetEngineConverter(bsp.MapType);
				if (engineConverter == null)
				{
					logger.Log($"Skipping {bsp.MapName}.bsp (unsupported BSP format: {bsp.MapType})");
					continue;
				}

				logger.Log($"Converting {bsp.MapName}.bsp...");

				var builder = new SourceBspBuilder(bsp.MapName, options.oldBSP, contentManager.ContentDir);
				engineConverter.Convert(bsp, builder);

				WriteBSP(builder, bsp.MapName);
				GenerateZones(builder.Bsp, bsp);
			}

			contentManager.Dispose();
		}

		// Engine converters are created on first use and reused for every BSP in the input file, since they
		// load assets shared by all of them (e.g. Quake 3 shaders).
		private IEngineConverter? GetEngineConverter(MapType mapType)
		{
			if (mapType.IsSubtypeOf(MapType.Quake3))
				return q3Converter ??= new Q3Converter(options, logger, contentManager);

			return null;
		}

		private void WriteBSP(SourceBspBuilder builder, string mapName)
		{
			var mapsDir = Path.Combine(options.outputDir, "maps");
			if (!Directory.Exists(mapsDir))
				Directory.CreateDirectory(mapsDir);

			var bspPath = Path.Combine(mapsDir, $"{options.prefix}{mapName}.bsp");
			builder.Write(bspPath, options.compress);

			logger.Log($"Wrote BSP File: {bspPath}");
		}

		private void GenerateZones(BSP sourceBsp, BSP inputBsp)
		{
			if (options.ignoreZones)
				return;

			// TODO: ZoneGenerator still reads zone geometry from the input BSP's brushes, which only works for Quake 3
			var zoneGenerator = new ZoneGenerator(sourceBsp, inputBsp, logger);
			var zoneDefs = zoneGenerator.Generate();

			var zonesDir = Path.Combine(options.outputDir, "maps", "zones", "local");
			if (!Directory.Exists(zonesDir))
				Directory.CreateDirectory(zonesDir);

			var zonePath = Path.Combine(zonesDir, $"{options.prefix}{inputBsp.MapName}.json");
			ZoneWriter.WriteToFile(zoneDefs, zonePath);

			logger.Log($"Wrote Zone File: {zonePath}");
		}
	}
}
