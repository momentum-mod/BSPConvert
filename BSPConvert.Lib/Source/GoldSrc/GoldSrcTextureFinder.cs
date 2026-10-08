using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BSPConvert.Lib.GoldSrc
{
	// Finds the pixels of a map's textures. Like the engine, a texture embedded in the BSP wins, then the WADs the
	// worldspawn "wad" key lists, in order. Those are absolute paths from the mapper's machine, so they're matched by
	// file name against the WADs found in the search directories, preferring the map's mod in a game folder like the
	// engine does. Other WADs of a listed name (another mod's version of it) come next, and any other WAD found is a
	// last resort, for maps that list a WAD under a different name or not at all.
	public class GoldSrcTextureFinder
	{
		private readonly GoldSrcBsp gs;
		private readonly ILogger logger;
		private readonly List<Wad3File> listedWads = new List<Wad3File>();
		// Other versions of the listed WADs, found under the same name in other folders
		private readonly List<Wad3File> otherListedWads;
		// Every other WAD in the search directories, opened on first use since a directory like a Half-Life install
		// holds many WADs the map doesn't use
		private readonly List<string> otherWadPaths;
		private readonly List<Wad3File> otherWads = new List<Wad3File>();
		private bool otherWadsOpened;

		// modName is the mod the map is for, whose folders come first in a game folder
		public GoldSrcTextureFinder(GoldSrcBsp gs, IEnumerable<string> searchDirs, string modName, ILogger logger)
		{
			this.gs = gs;
			this.logger = logger;

			var wadPaths = FindWadFiles(searchDirs, modName);
			var listedNames = GetListedWadNames();
			var listedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var otherListedWads = new List<Wad3File>();
			foreach (var name in listedNames)
			{
				if (!wadPaths.TryGetValue(name, out var paths))
				{
					logger.Log($"Warning: WAD not found: {name}");
					continue;
				}

				for (var i = 0; i < paths.Count; i++)
				{
					var wad = Wad3File.Open(paths[i]);
					if (wad == null)
					{
						logger.Log($"Warning: {paths[i]} isn't a WAD3 file");
						continue;
					}

					(i == 0 ? listedWads : otherListedWads).Add(wad);
					listedPaths.Add(paths[i]);
				}
			}

			this.otherListedWads = otherListedWads;
			otherWadPaths = wadPaths.Values.SelectMany(paths => paths).Where(path => !listedPaths.Contains(path)).ToList();
		}


		// Returns the texture's pixels, or null if it isn't embedded and no WAD has it
		public MipTexture? Find(int mipTexIndex)
		{
			return gs.GetEmbeddedMipTexture(mipTexIndex) ?? FindInWads(gs.MipTextures[mipTexIndex].name);
		}

		// Looks a texture up by name, for textures the map doesn't reference directly (other animation frames)
		public MipTexture? Find(string name)
		{
			for (var i = 0; i < gs.MipTextures.Length; i++)
			{
				if (gs.MipTextures[i].name.Equals(name, StringComparison.OrdinalIgnoreCase))
					return Find(i);
			}

			return FindInWads(name);
		}

		private MipTexture? FindInWads(string name)
		{
			if (string.IsNullOrEmpty(name))
				return null;

			foreach (var wad in listedWads)
			{
				if (wad.Contains(name))
					return wad.ReadMipTexture(name);
			}

			foreach (var wad in otherListedWads)
			{
				if (wad.Contains(name))
				{
					logger.Log($"Texture {name} isn't in the map's listed WADs, using {wad.FilePath}");
					return wad.ReadMipTexture(name);
				}
			}

			OpenOtherWads();
			foreach (var wad in otherWads)
			{
				if (wad.Contains(name))
				{
					logger.Log($"Texture {name} isn't in the map's listed WADs, using {wad.FilePath}");
					return wad.ReadMipTexture(name);
				}
			}

			return null;
		}

		private void OpenOtherWads()
		{
			if (otherWadsOpened)
				return;

			otherWadsOpened = true;
			foreach (var path in otherWadPaths)
			{
				var wad = Wad3File.Open(path);
				if (wad != null)
					otherWads.Add(wad);
			}
		}

		// WAD file names from the worldspawn "wad" key ("\half-life\valve\halflife.wad;\...\other.wad;"), in order
		private List<string> GetListedWadNames()
		{
			var worldspawn = gs.Entities.FirstOrDefault(e => e.ClassName == "worldspawn");
			var wadKey = worldspawn?["wad"];
			if (string.IsNullOrEmpty(wadKey))
				wadKey = worldspawn?["_wad"];
			if (string.IsNullOrEmpty(wadKey))
				return new List<string>();

			return wadKey
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(path => path.Replace('\\', '/'))
				.Select(path => path.Substring(path.LastIndexOf('/') + 1))
				.Where(name => name.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		// WAD file name -> paths, from every search directory (recursively), best first: earlier directories, and
		// within one the map's mod's folders (then its downloads and Half-Life's), as the engine would look in them
		private static Dictionary<string, List<string>> FindWadFiles(IEnumerable<string> searchDirs, string modName)
		{
			var preferredMods = new[] { modName, modName + "_downloads", "valve", "valve_downloads" };
			var wadPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var enumerationOptions = new EnumerationOptions
			{
				RecurseSubdirectories = true,
				MatchCasing = MatchCasing.CaseInsensitive,
				IgnoreInaccessible = true
			};

			foreach (var dir in searchDirs)
			{
				if (!Directory.Exists(dir))
					continue;

				int GetModRank(string path)
				{
					var topDir = Path.GetRelativePath(dir, path).Split(Path.DirectorySeparatorChar)[0];
					var rank = Array.FindIndex(preferredMods, mod => mod.Equals(topDir, StringComparison.OrdinalIgnoreCase));
					return rank >= 0 ? rank : preferredMods.Length;
				}

				var paths = Directory.EnumerateFiles(dir, "*.wad", enumerationOptions)
					.Select(Path.GetFullPath)
					.OrderBy(GetModRank)
					.ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
				foreach (var path in paths)
				{
					if (!seen.Add(path))
						continue;

					var name = Path.GetFileName(path);
					if (!wadPaths.TryGetValue(name, out var namePaths))
						wadPaths[name] = namePaths = new List<string>();
					namePaths.Add(path);
				}
			}

			return wadPaths;
		}
	}
}
