using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BSPConvert.Lib.GoldSrc
{
	// Finds the sound files a map's entities play, by their path under a sound folder ("ambience/drips.wav"). Like the
	// engine, which looks in its mod's folder, then its downloads, then Half-Life's, each search directory is looked
	// in as a mod folder (<dir>/sound) and as a game folder holding mod folders (<dir>/<mod>/sound).
	public class GoldSrcSoundFinder
	{
		private readonly List<string> soundDirs;

		// modName is the mod the map is for, whose folders come first in a game folder
		public GoldSrcSoundFinder(IEnumerable<string> searchDirs, string modName)
		{
			var preferredMods = new[] { modName, modName + "_downloads", "valve", "valve_downloads" };
			var candidateDirs = new List<string>();
			foreach (var dir in searchDirs.Where(Directory.Exists))
			{
				candidateDirs.Add(Path.Combine(dir, "sound"));

				var modDirs = Directory.EnumerateDirectories(dir)
					.OrderBy(modDir =>
					{
						var index = Array.FindIndex(preferredMods, mod => mod.Equals(Path.GetFileName(modDir), StringComparison.OrdinalIgnoreCase));
						return index >= 0 ? index : preferredMods.Length;
					})
					.ThenBy(modDir => modDir, StringComparer.OrdinalIgnoreCase);
				foreach (var modDir in modDirs)
					candidateDirs.Add(Path.Combine(modDir, "sound"));
			}

			soundDirs = candidateDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}

		// The file of a sound, or null if no search directory has it
		public string? Find(string soundPath)
		{
			var relativePath = soundPath.Replace('/', Path.DirectorySeparatorChar);
			foreach (var soundDir in soundDirs)
			{
				var file = Path.Combine(soundDir, relativePath);
				if (File.Exists(file))
					return file;
			}

			return null;
		}
	}
}
