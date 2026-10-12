using System.Globalization;

namespace BSPConvert.Lib
{
	// One folder of Quake 3 content: a map's extracted pk3, Quake 3's paks extracted into Q3Content, a mod's into
	// Q3Mods/<mod>, or CustomContent. Its images and shaders are read when first needed.
	public class Q3ContentFolder
	{
		// Quake 3 loads a .jpg when there's no .tga
		private static readonly string[] ImageExtensions = { ".tga", ".jpg" };

		private Dictionary<string, string>? images;
		private string[]? shaderFiles;
		private HashSet<string>? shaderNames;

		// pakName is the pk3 the engine would load the folder's files from (e.g. "pak0.pk3"), which decides where it's
		// searched among the other pk3s of its game folder. Null for loose files, which are searched after the pk3s.
		public Q3ContentFolder(string directory, string? pakName, string description, bool isMap = false)
		{
			Directory = directory;
			PakName = pakName;
			Description = description;
			IsMap = isMap;
		}

		public string Directory { get; }
		public string? PakName { get; }
		// For the log, e.g. "Q3Content" or "nood-aDr.pk3"
		public string Description { get; }
		// The map's own content, which conversion adds its output to
		public bool IsMap { get; }

		// Images by texture path: lower case, forward slashes, no extension ("textures/base_wall/basewall01")
		public IReadOnlyDictionary<string, string> Images => images ??= LoadImages();

		// The folder's shader scripts (scripts/*.shader), ordered by file name
		public IReadOnlyList<string> ShaderFiles => shaderFiles ??= LoadShaderFiles();

		// Names of the shaders its scripts define
		public IReadOnlySet<string> ShaderNames => shaderNames ??= LoadShaderNames();

		// Whether the folder has a shader or image by this name (a texture path, see Images)
		public bool Provides(string texture)
		{
			return ShaderNames.Contains(texture) || Images.ContainsKey(texture);
		}

		// The file at a path relative to the folder ("sound/world/jumppad.wav"), or null
		public string? FindFile(string relativePath)
		{
			var path = Path.Combine(Directory, relativePath);
			return File.Exists(path) ? path : null;
		}

		// Forgets what was read, for a folder files were added to since
		public void Refresh()
		{
			images = null;
			shaderFiles = null;
			shaderNames = null;
		}

		// Lower case, forward slashes and no image extension, as texture paths are looked up
		public static string NormalizeTexturePath(string texturePath)
		{
			var path = texturePath.Replace('\\', '/').ToLower(CultureInfo.InvariantCulture);
			foreach (var extension in ImageExtensions)
			{
				if (path.EndsWith(extension, StringComparison.Ordinal))
					return path.Substring(0, path.Length - extension.Length);
			}

			return path;
		}

		private Dictionary<string, string> LoadImages()
		{
			var found = new Dictionary<string, string>();
			if (!System.IO.Directory.Exists(Directory))
				return found;

			foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.*", SearchOption.AllDirectories))
			{
				var extension = Path.GetExtension(file).ToLowerInvariant();
				var rank = Array.IndexOf(ImageExtensions, extension);
				if (rank < 0)
					continue;

				var texturePath = NormalizeTexturePath(Path.GetRelativePath(Directory, file));
				if (!found.TryGetValue(texturePath, out var existing) ||
					rank < Array.IndexOf(ImageExtensions, Path.GetExtension(existing).ToLowerInvariant()))
					found[texturePath] = file;
			}

			return found;
		}

		private string[] LoadShaderFiles()
		{
			var scripts = Path.Combine(Directory, "scripts");
			if (!System.IO.Directory.Exists(scripts))
				return Array.Empty<string>();

			return System.IO.Directory.GetFiles(scripts, "*.shader")
				.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		private HashSet<string> LoadShaderNames()
		{
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var file in ShaderFiles)
				names.UnionWith(new ShaderParser(file).ParseShaders().Keys);

			return names;
		}
	}

	// Where a Quake 3 map's textures, shaders and sounds are looked up, in the order Defrag's engine (oDFe, a Quake3e
	// fork) searches them: the mod's game folder before baseq3, and within a game folder its pk3s from the last to the
	// first by name, then its loose files. So a map in z-map.pk3 overrides Quake 3's pak0.pk3 and one in a-map.pk3
	// doesn't, as in game. The map's pk3 sits in the game folder it's played from, the mod's when there is one.
	// CustomContent isn't part of the game, so it's searched last and only fills gaps.
	public class Q3SearchPath
	{
		public Q3SearchPath(Q3ContentFolder map, Q3ContentFolder? baseGame, Q3ContentFolder? mod = null, Q3ContentFolder? custom = null)
		{
			Map = map;
			Mod = mod;

			var folders = new List<Q3ContentFolder>();
			if (mod != null)
			{
				folders.AddRange(Order(map, mod));
				folders.AddRange(Order(baseGame));
			}
			else
			{
				folders.AddRange(Order(map, baseGame));
			}

			if (custom != null)
				folders.Add(custom);

			Folders = folders;
		}

		// Highest priority first
		public IReadOnlyList<Q3ContentFolder> Folders { get; }

		public Q3ContentFolder Map { get; }

		public Q3ContentFolder? Mod { get; }

		// The image the engine would load for a texture path (any casing, with or without extension), and the folder
		// it's in, or null when no folder has it
		public (Q3ContentFolder Folder, string Path)? FindImage(string texturePath)
		{
			var key = Q3ContentFolder.NormalizeTexturePath(texturePath);
			foreach (var folder in Folders)
			{
				if (folder.Images.TryGetValue(key, out var path))
					return (folder, path);
			}

			return null;
		}

		// The file at a path relative to the game folder ("sound/world/jumppad.wav") the engine would load, or null
		public string? FindFile(string relativePath)
		{
			foreach (var folder in Folders)
			{
				var path = folder.FindFile(relativePath);
				if (path != null)
					return path;
			}

			return null;
		}

		// Whether any folder has a shader or image for the texture
		public bool Provides(string texture)
		{
			var key = Q3ContentFolder.NormalizeTexturePath(texture);
			return Folders.Any(folder => folder.Provides(key));
		}

		// The shader scripts the engine reads, in its order: each file name once, from the first folder that has it, so
		// a pk3's scripts/common.shader replaces the whole of a lower one's. Where two files define a shader, the earlier
		// file's definition is the one used (see ShaderLoader), as oDFe's shader text hash table finds it first.
		public IReadOnlyList<string> GetShaderFiles()
		{
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var files = new List<string>();
			foreach (var folder in Folders)
			{
				foreach (var file in folder.ShaderFiles)
				{
					if (names.Add(Path.GetFileName(file)))
						files.Add(file);
				}
			}

			return files;
		}

		// A game folder's content, highest priority first: its pk3s from the last to the first by name (the engine
		// puts each one it loads in front of the ones before), then its loose files
		private static IEnumerable<Q3ContentFolder> Order(params Q3ContentFolder?[] gameFolder)
		{
			return gameFolder
				.OfType<Q3ContentFolder>()
				.OrderBy(folder => folder.PakName == null)
				.ThenByDescending(folder => folder.PakName, PakNameComparer.Instance);
		}
	}

	// How the engine sorts pk3 names (FS_PathCmp): case-insensitive, with \ and : as /
	public class PakNameComparer : IComparer<string?>
	{
		public static readonly PakNameComparer Instance = new PakNameComparer();

		public int Compare(string? x, string? y)
		{
			return string.CompareOrdinal(Normalize(x), Normalize(y));
		}

		private static string? Normalize(string? name)
		{
			return name?.ToLowerInvariant().Replace('\\', '/').Replace(':', '/');
		}
	}
}
