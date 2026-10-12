namespace BSPConvert.Lib
{
	// What Q3Mods.Detect found: the mod the map needs (null for Quake 3 alone), the textures only it has, and the
	// textures nothing has
	public record Q3ModDetection(Q3ContentFolder? Mod, IReadOnlyList<string> TexturesOnlyInMod, IReadOnlyList<string> Missing);

	// Quake 3 mods and expansions (Team Arena) whose content maps can use besides Quake 3's. Each is extracted into a
	// folder of Q3Mods named after its game folder (Q3Mods/missionpack), as Quake 3's paks are into Q3Content.
	public static class Q3Mods
	{
		// What --q3mod takes for Quake 3's own content
		public const string BaseGame = "baseq3";

		// What the engine would call the mod's pk3, for ordering it against the map's (see Q3SearchPath)
		private const string ModPakName = "pak0.pk3";

		private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
		{
			[BaseGame] = "Quake 3",
			["missionpack"] = "Team Arena",
		};

		public static readonly string DefaultDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Q3Mods");

		// The game's name: "Team Arena" for missionpack, the folder's name for mods it doesn't know
		public static string GetDisplayName(string game)
		{
			return KnownNames.TryGetValue(game, out var name) ? name : game;
		}

		// The mods in a Q3Mods folder: its subfolders with files in them, by name
		public static IReadOnlyList<string> List(string modsDirectory)
		{
			if (!Directory.Exists(modsDirectory))
				return Array.Empty<string>();

			return Directory.GetDirectories(modsDirectory)
				.Where(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
				.Select(Path.GetFileName)
				.OfType<string>()
				.Order(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		// A mod's folder in a Q3Mods folder, as Q3SearchPath takes it
		public static Q3ContentFolder GetContentFolder(string modsDirectory, string mod)
		{
			return new Q3ContentFolder(Path.Combine(modsDirectory, mod), ModPakName, $"Q3Mods/{mod}");
		}

		// The mod a map needs: of the textures it uses that neither it nor Quake 3 has, the mod that has the most of them.
		// Null when no mod has any, since the map then only needs Quake 3 (or pk3s we don't have, see Missing).
		// Ties go to the mod listed first.
		public static Q3ModDetection Detect(IEnumerable<string> textures, Q3SearchPath basePath, IEnumerable<Q3ContentFolder> mods)
		{
			var unresolved = textures
				.Select(Q3ContentFolder.NormalizeTexturePath)
				.Distinct()
				.Where(texture => !basePath.Provides(texture))
				.Order(StringComparer.Ordinal)
				.ToList();

			Q3ContentFolder? best = null;
			var bestTextures = new List<string>();
			if (unresolved.Count > 0)
			{
				foreach (var mod in mods)
				{
					var provided = unresolved.Where(mod.Provides).ToList();
					if (provided.Count > bestTextures.Count)
					{
						best = mod;
						bestTextures = provided;
					}
				}
			}

			var missing = unresolved.Except(bestTextures).ToList();
			return new Q3ModDetection(best, bestTextures, missing);
		}
	}
}
