using LibBSP;
using System;
using System.IO;
using System.Linq;
using BSPConvert.Lib.Zones;

namespace BSPConvert.Lib
{
	// Options shared by every input engine. Engine-specific options live in their own class (e.g. q3).
	public class BSPConverterOptions
	{
		public string inputFile;
		public string outputDir;
		// Prefix for the converted BSP's file name
		public string prefix;
		// Optional filter to convert only specific BSP(s) from a multi-BSP pk3. Matched against each
		// BSP's map name (without extension), case-insensitively. Null/empty converts every BSP.
		public string[] mapFilter;
		// Export materials into folders instead of embedding them in the BSP's pakfile
		public bool noPak;
		// LZMA compress the BSP's lumps
		public bool compress;
		public bool oldBSP;
		public bool ignoreZones;
		// Minimum trigger_hurt damage that respawns the player instead of hurting them
		public int minDamageToRespawnPlayer;
		// Uniform multiplier applied to the map's geometry (vertices, plane distances, bounding boxes) and
		// position-based entity data before conversion, so the converted map is bigger/smaller than the
		// original. 1 (default) makes no change. See Q3Converter.ScaleQuakeBsp.
		public float scale = 1f;

		public Q3ConverterOptions q3 = new Q3ConverterOptions();
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
