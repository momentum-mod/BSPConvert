using LibBSP;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using BSPConvert.Lib.GoldSrc;
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
		// A GoldSrc BSP whose clip hulls are copied into the input instead of converting it: a Strata BSP recompiled
		// from a converted copy of that map (see GoldSrcHullTransfer)
		public string? goldSrcHullSource;

		public Q3ConverterOptions q3 = new Q3ConverterOptions();
		public GoldSrcConverterOptions goldSrc = new GoldSrcConverterOptions();
	}

	// Entry point for a conversion: loads the input file's BSP(s), hands each one to the converter for its
	// engine (see IEngineConverter), then writes the resulting Source BSP and its zone file. Reports its progress
	// (see ProgressTracker), and when the cancellation token is cancelled stops at the next stage or item (texture,
	// model...) with an OperationCanceledException.
	public class BSPConverter
	{
		// Parts of the whole conversion loading the input takes (extracting an archive, reading its BSPs)
		private const float LoadShare = 0.005f;
		private const float ArchiveLoadShare = 0.01f;
		// Parts of each map's conversion writing its BSP takes. LZMA compressing every lump takes a few times longer.
		private const float WriteShare = 0.01f;
		private const float CompressedWriteShare = 0.04f;

		private BSPConverterOptions options;
		private ILogger logger;
		private ProgressTracker progress;

		private ContentManager contentManager;
		private Q3Converter? q3Converter;
		private GoldSrcConverter? goldSrcConverter;

		public BSPConverter(BSPConverterOptions options, ILogger logger, IProgress<ConversionProgress>? progress = null, CancellationToken cancellation = default)
		{
			this.options = options;
			this.logger = logger;
			this.progress = new ProgressTracker(progress, cancellation);
		}

		public void Convert()
		{
			if (!File.Exists(options.inputFile))
			{
				logger.Log($"Error: {options.inputFile} doesn't exist");
				return;
			}

			if (options.goldSrcHullSource != null)
			{
				TransferGoldSrcHulls();
				return;
			}

			var isArchive = !Path.GetExtension(options.inputFile).Equals(".bsp", StringComparison.OrdinalIgnoreCase);
			var loadShare = isArchive ? ArchiveLoadShare : LoadShare;
			progress.BeginSection(0f, loadShare);
			progress.Stage(isArchive ? "Extracting " + Path.GetFileName(options.inputFile) : "Reading " + Path.GetFileName(options.inputFile), 1f);

			contentManager = new ContentManager(options.inputFile);
			try
			{
				var maps = SelectMaps();
				var mapShare = (1f - loadShare) / Math.Max(maps.Count, 1);
				var writeShare = options.compress ? CompressedWriteShare : WriteShare;
				for (var i = 0; i < maps.Count; i++)
				{
					var (bsp, engineConverter) = maps[i];
					var mapStart = loadShare + i * mapShare;
					var writeStart = mapStart + mapShare * (1f - writeShare);
					progress.SetMap(bsp.MapName, i, maps.Count);

					logger.Log(maps.Count > 1 ? $"Converting {bsp.MapName}.bsp ({i + 1}/{maps.Count})..." : $"Converting {bsp.MapName}.bsp...");

					progress.BeginSection(mapStart, writeStart);
					var builder = new SourceBspBuilder(bsp.MapName, options.oldBSP, contentManager.ContentDir, logger);
					engineConverter.Convert(bsp, builder);

					// Only Defrag maps carry timer triggers the zone generator understands
					var generateZones = bsp.MapType.IsSubtypeOf(MapType.Quake3) && !options.ignoreZones;
					progress.BeginSection(writeStart, mapStart + mapShare);
					progress.Stage("Writing BSP", generateZones ? 0.9f : 1f);
					WriteBSP(builder, bsp.MapName);

					if (generateZones)
					{
						progress.Stage("Writing zones", 0.1f);
						GenerateZones(builder, bsp.MapName);
					}
				}

				progress.Finish();
			}
			finally
			{
				contentManager.Dispose();
			}
		}

		// The input's BSPs that will be converted, with the converter for each. Logs why the others are skipped.
		private List<(BSP bsp, IEngineConverter converter)> SelectMaps()
		{
			var maps = new List<(BSP, IEngineConverter)>();
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

				maps.Add((bsp, engineConverter));
			}

			return maps;
		}

		// Engine converters are created on first use and reused for every BSP in the input file, since they
		// load assets shared by all of them (e.g. Quake 3 shaders).
		private IEngineConverter? GetEngineConverter(MapType mapType)
		{
			if (mapType.IsSubtypeOf(MapType.Quake3))
				return q3Converter ??= new Q3Converter(options, logger, progress, contentManager);

			if (mapType.IsSubtypeOf(MapType.GoldSrc))
				return goldSrcConverter ??= new GoldSrcConverter(options, logger, progress, contentManager);

			return null;
		}

		private void TransferGoldSrcHulls()
		{
			if (!File.Exists(options.goldSrcHullSource))
			{
				logger.Log($"Error: {options.goldSrcHullSource} doesn't exist");
				return;
			}

			var mapsDir = Path.Combine(options.outputDir, "maps");
			Directory.CreateDirectory(mapsDir);

			progress.Stage("Transferring clip hulls", 1f);
			var bspPath = Path.Combine(mapsDir, $"{options.prefix}{Path.GetFileNameWithoutExtension(options.inputFile)}.bsp");
			if (new GoldSrcHullTransfer(logger).Transfer(options.goldSrcHullSource, options.inputFile, bspPath))
				logger.Log($"Wrote {bspPath}");

			progress.Finish();
		}

		private void WriteBSP(SourceBspBuilder builder, string mapName)
		{
			var mapsDir = Path.Combine(options.outputDir, "maps");
			if (!Directory.Exists(mapsDir))
				Directory.CreateDirectory(mapsDir);

			var bspPath = Path.Combine(mapsDir, $"{options.prefix}{mapName}.bsp");
			builder.Write(bspPath, options.compress);

			logger.Log($"Wrote {bspPath}");
		}

		private void GenerateZones(SourceBspBuilder builder, string mapName)
		{
			var zoneGenerator = new ZoneGenerator(builder, logger);
			var zoneDefs = zoneGenerator.Generate();

			var zonesDir = Path.Combine(options.outputDir, "maps", "zones", "local");
			if (!Directory.Exists(zonesDir))
				Directory.CreateDirectory(zonesDir);

			var zonePath = Path.Combine(zonesDir, $"{options.prefix}{mapName}.json");
			ZoneWriter.WriteToFile(zoneDefs, zonePath);

			logger.Log($"Wrote {zonePath}");
		}
	}
}
