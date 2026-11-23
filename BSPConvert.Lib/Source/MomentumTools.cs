using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BSPConvert.Lib
{
	// Finds Momentum Mod's compile tools in its bin/win64 folder
	public static class MomentumTools
	{
		// Where Steam installs Momentum Mod by default
		public static readonly string DefaultMomentumDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Momentum Mod Playtest");

		// The given tool, or the one of the game the output folder is in, or of a default Steam Momentum Mod install
		public static string? Find(string exeName, string? givenPath, string outputDir)
		{
			if (!string.IsNullOrEmpty(givenPath))
				return File.Exists(givenPath) ? givenPath : null;

			var candidates = new List<string>();
			if (Path.GetDirectoryName(Path.GetFullPath(outputDir)) is string gameDir)
				candidates.Add(Path.Combine(gameDir, "bin", "win64", exeName));
			candidates.Add(Path.Combine(DefaultMomentumDir, "bin", "win64", exeName));

			return candidates.FirstOrDefault(File.Exists);
		}
	}
}
