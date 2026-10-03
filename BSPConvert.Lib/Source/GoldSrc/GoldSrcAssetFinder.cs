using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BSPConvert.Lib.GoldSrc
{
	// Finds the files a map's entities use (sounds, sprites) by their path in the mod ("sound/ambience/drips.wav"). Like
	// the engine, which looks in its mod's folder, then its downloads, then Half-Life's, each search directory is
	// looked in as a mod folder (<dir>/<path>) and as a game folder holding mod folders (<dir>/<mod>/<path>).
	public class GoldSrcAssetFinder
	{
		private readonly List<string> modDirs;

		// modName is the mod the map is for, whose folders come first in a game folder
		public GoldSrcAssetFinder(IEnumerable<string> searchDirs, string modName)
		{
			var preferredMods = new[] { modName, modName + "_downloads", "valve", "valve_downloads" };
			var candidateDirs = new List<string>();
			foreach (var dir in searchDirs.Where(Directory.Exists))
			{
				candidateDirs.Add(dir);

				var subDirs = Directory.EnumerateDirectories(dir)
					.OrderBy(modDir =>
					{
						var index = Array.FindIndex(preferredMods, mod => mod.Equals(Path.GetFileName(modDir), StringComparison.OrdinalIgnoreCase));
						return index >= 0 ? index : preferredMods.Length;
					})
					.ThenBy(modDir => modDir, StringComparer.OrdinalIgnoreCase);
				candidateDirs.AddRange(subDirs);
			}

			modDirs = candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}

		// The file at a path in the mod, or null if no search directory has it
		public string? Find(string path)
		{
			var relativePath = path.Replace('/', Path.DirectorySeparatorChar);
			foreach (var modDir in modDirs)
			{
				var file = Path.Combine(modDir, relativePath);
				if (File.Exists(file))
					return file;
			}

			return null;
		}
	}
}
