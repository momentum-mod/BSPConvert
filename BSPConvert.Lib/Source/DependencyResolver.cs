using LibBSP;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;

namespace BSPConvert.Lib
{
	// Detects Quake 3 texture sets a map references but doesn't ship, then downloads the maps
	// that provide them and extracts their textures/shaders into the content dir so they get
	// converted and embedded into the output BSP like any other asset.
	//
	// Note on the download host: q3df.org (the canonical source) now sits behind a Cloudflare
	// JS challenge that returns HTTP 403 to non-browser clients, so it can't be used from an
	// automated tool. defrag.racing mirrors the same q3df map database and serves pk3s directly
	// (case-insensitive) with no challenge, so it's used as the resolver's download source.
	public class DependencyResolver
	{
		private readonly string contentDir;
		private readonly string q3ContentDir;
		private readonly ILogger logger;

		// {0} = map/texture-set name (lower-cased). The host is case-insensitive.
		private const string DownloadUrlFormat = "https://dl.defrag.racing/downloads/maps/{0}.pk3";

		// Only these top-level folders are extracted from a dependency pk3. We deliberately skip
		// maps/, levelshots/, sound/, etc. so the output pak isn't bloated with the dependency's
		// whole map - we only need the assets it provides.
		private static readonly string[] extractedFolders = { "textures/", "scripts/", "env/" };

		private static readonly string[] imageExtensions = { ".tga", ".jpg", ".jpeg", ".png" };

		private static readonly HttpClient httpClient = CreateHttpClient();

		public DependencyResolver(string contentDir, string q3ContentDir, ILogger logger)
		{
			this.contentDir = contentDir;
			this.q3ContentDir = q3ContentDir;
			this.logger = logger;
		}

		private static HttpClient CreateHttpClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
			// Send a browser-like User-Agent so mirrors don't reject the request.
			client.DefaultRequestHeaders.UserAgent.ParseAdd(
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) BSPConvert");
			return client;
		}

		// Returns the number of dependencies successfully downloaded.
		public int DownloadMissingDependencies(IEnumerable<BSP> bspFiles, IReadOnlyDictionary<string, Shader> shaderDict)
		{
			var missingSets = GetMissingTextureSets(bspFiles, shaderDict);
			if (missingSets.Count == 0)
				return 0;

			logger.Log($"Detected {missingSets.Count} missing external texture dependencies: {string.Join(", ", missingSets.OrderBy(s => s))}");

			var downloaded = 0;
			foreach (var set in missingSets.OrderBy(s => s))
			{
				if (TryDownloadAndExtract(set))
					downloaded++;
			}

			return downloaded;
		}

		// A texture set is "missing" when the map references it but neither the pk3 nor the Q3Content
		// folder provides that set's folder. This mirrors how q3df computes a map's dependency list.
		private HashSet<string> GetMissingTextureSets(IEnumerable<BSP> bspFiles, IReadOnlyDictionary<string, Shader> shaderDict)
		{
			var localSets = GetLocalTextureSets(contentDir);
			localSets.UnionWith(GetLocalTextureSets(q3ContentDir));

			var neededSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var bsp in bspFiles)
			{
				foreach (var texture in bsp.Textures)
				{
					AddTextureSet(neededSets, texture.Name);

					// A shader may pull images from other sets; include those too.
					if (shaderDict.TryGetValue(texture.Name, out var shader))
					{
						foreach (var stage in shader.GetImageStages())
							foreach (var image in stage.bundles[0].images)
								AddTextureSet(neededSets, image);
					}
				}
			}

			neededSets.ExceptWith(localSets);
			return neededSets;
		}

		// Collects the top-level folder names under <root>/textures (e.g. "basedm7run").
		private static HashSet<string> GetLocalTextureSets(string root)
		{
			var sets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var texturesDir = Path.Combine(root, "textures");
			if (Directory.Exists(texturesDir))
			{
				foreach (var dir in Directory.GetDirectories(texturesDir))
					sets.Add(Path.GetFileName(dir));
			}

			return sets;
		}

		// Extracts the texture-set name (folder after "textures/") from an asset path, if any.
		private static void AddTextureSet(HashSet<string> sets, string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			var parts = assetPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 3 && parts[0].Equals("textures", StringComparison.OrdinalIgnoreCase))
				sets.Add(parts[1]);
		}

		private bool TryDownloadAndExtract(string set)
		{
			var pk3Path = GetCachedPk3Path(set);
			try
			{
				if (!File.Exists(pk3Path) || new FileInfo(pk3Path).Length == 0)
				{
					var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, DownloadUrlFormat, Uri.EscapeDataString(set.ToLowerInvariant()));
					logger.Log($"Downloading external map dependency '{set}' from {url}...");

					if (!TryDownloadFile(url, pk3Path))
					{
						logger.Log($"Warning: Could not download dependency '{set}' (not found on mirror). Its textures will be missing.");
						return false;
					}
				}
				else
				{
					logger.Log($"Using cached external map dependency '{set}'.");
				}

				var extracted = ExtractDependencyAssets(pk3Path, set);
				logger.Log($"Extracted {extracted} asset(s) from dependency '{set}'.");
				return true;
			}
			catch (Exception e)
			{
				logger.Log($"Warning: Failed to resolve dependency '{set}': {e.Message}");
				return false;
			}
		}

		private static string GetCachedPk3Path(string set)
		{
			var cacheDir = Path.Combine(Path.GetTempPath(), "BSPConvertDependencies");
			Directory.CreateDirectory(cacheDir);
			return Path.Combine(cacheDir, set.ToLowerInvariant() + ".pk3");
		}

		private bool TryDownloadFile(string url, string destPath)
		{
			using var response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
			if (!response.IsSuccessStatusCode)
				return false;

			var tempPath = destPath + ".tmp";
			using (var httpStream = response.Content.ReadAsStream())
			using (var fileStream = File.Create(tempPath))
				httpStream.CopyTo(fileStream);

			File.Move(tempPath, destPath, true);
			return true;
		}

		// Extracts only the asset folders we care about, and only the missing set's textures, into
		// the content dir without overwriting files the map already ships. Returns the number of
		// files written. Shader scripts are always extracted (they're tiny and define how the
		// dependency's textures should look once embedded).
		private int ExtractDependencyAssets(string pk3Path, string set)
		{
			var setPrefix = $"textures/{set.ToLowerInvariant()}/";
			var extracted = 0;

			using var archive = ZipFile.OpenRead(pk3Path);
			foreach (var entry in archive.Entries)
			{
				if (string.IsNullOrEmpty(entry.Name)) // Directory entry
					continue;

				var entryPath = entry.FullName.Replace('\\', '/').ToLowerInvariant();

				if (!extractedFolders.Any(f => entryPath.StartsWith(f, StringComparison.Ordinal)))
					continue;

				// For textures, only pull the set this dependency is providing (keeps the pak lean).
				var isImage = imageExtensions.Contains(Path.GetExtension(entryPath), StringComparer.Ordinal);
				if (entryPath.StartsWith("textures/", StringComparison.Ordinal) && isImage &&
					!entryPath.StartsWith(setPrefix, StringComparison.Ordinal))
					continue;

				var destPath = Path.Combine(contentDir, entryPath.Replace('/', Path.DirectorySeparatorChar));
				if (File.Exists(destPath)) // Don't clobber the map's own assets
					continue;

				Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
				entry.ExtractToFile(destPath, false);
				extracted++;
			}

			return extracted;
		}
	}
}
