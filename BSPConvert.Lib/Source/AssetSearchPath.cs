using System.Collections.Generic;
using System.IO;

namespace BSPConvert.Lib
{
	// Read-only directories searched in priority order for assets a map references but doesn't ship itself
	// (e.g. a base game's content, then user-supplied content). Assets are looked up by their path relative to
	// each directory ("sound/world/jumppad.wav"), the same way the game's own filesystem resolves them.
	public class AssetSearchPath
	{
		public IReadOnlyList<string> Directories { get; }

		public AssetSearchPath(params string[] directories)
		{
			Directories = directories;
		}

		// Full path of the asset in the first directory that has it, or null if none do
		public string? FindFile(string relativePath)
		{
			foreach (var directory in Directories)
			{
				var path = Path.Combine(directory, relativePath);
				if (File.Exists(path))
					return path;
			}

			return null;
		}
	}
}
