using BSPConvert.Lib;

namespace BSPConvert.Test
{
	public class Q3SearchPathTests
	{
		private string root;

		[SetUp]
		public void Setup()
		{
			root = Path.Combine(Path.GetTempPath(), "BSPConvertTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
		}

		[TearDown]
		public void TearDown()
		{
			Directory.Delete(root, true);
		}

		private string CreateFile(string relativePath, string content = "")
		{
			var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
		}

		private Q3ContentFolder Folder(string name, string? pakName, bool isMap = false)
		{
			var directory = Path.Combine(root, name);
			Directory.CreateDirectory(directory);
			return new Q3ContentFolder(directory, pakName, name, isMap);
		}

		private static string Shader(string name, string image)
		{
			return $"{name}\n{{\n\t{{\n\t\tmap {image}\n\t}}\n}}\n";
		}

		[Test]
		public void OrdersFoldersLikeTheEngine()
		{
			var baseGame = Folder("Q3Content", "pak0.pk3");
			var custom = Folder("CustomContent", null);

			// pk3s are searched from the last to the first by name, case-insensitively
			var after = Folder("after", "Z-map.pk3", isMap: true);
			Assert.That(new Q3SearchPath(after, baseGame, null, custom).Folders, Is.EqualTo(new[] { after, baseGame, custom }));

			var before = Folder("before", "a-map.pk3", isMap: true);
			Assert.That(new Q3SearchPath(before, baseGame, null, custom).Folders, Is.EqualTo(new[] { baseGame, before, custom }));

			// A .bsp's loose files come after the game folder's pk3s
			var loose = Folder("loose", null, isMap: true);
			Assert.That(new Q3SearchPath(loose, baseGame, null, custom).Folders, Is.EqualTo(new[] { baseGame, loose, custom }));

			// The mod's game folder (with the map in it) before baseq3's
			var mod = Folder("missionpack", "pak0.pk3");
			Assert.That(new Q3SearchPath(before, baseGame, mod, custom).Folders, Is.EqualTo(new[] { mod, before, baseGame, custom }));
		}

		[Test]
		public void FindsImagesInTheFirstFolderWithThem()
		{
			var map = Folder("map", "z-map.pk3", isMap: true);
			var baseGame = Folder("Q3Content", "pak0.pk3");
			var custom = Folder("CustomContent", null);
			var mapImage = CreateFile("map/textures/base_wall/a.jpg");
			CreateFile("Q3Content/textures/base_wall/a.tga");
			var baseTga = CreateFile("Q3Content/textures/base_wall/b.tga");
			CreateFile("Q3Content/textures/base_wall/b.jpg");
			var customImage = CreateFile("CustomContent/textures/other/c.TGA");

			var searchPath = new Q3SearchPath(map, baseGame, null, custom);

			Assert.That(searchPath.FindImage("textures/base_wall/a")?.Path, Is.EqualTo(mapImage));
			Assert.That(searchPath.FindImage("textures/base_wall/a")?.Folder.IsMap, Is.True);
			// .tga before .jpg, and any casing or extension
			Assert.That(searchPath.FindImage("Textures/Base_Wall/B.jpg")?.Path, Is.EqualTo(baseTga));
			Assert.That(searchPath.FindImage("textures/other/c")?.Path, Is.EqualTo(customImage));
			Assert.That(searchPath.FindImage("textures/other/missing"), Is.Null);
		}

		[Test]
		public void LoadsShadersLikeTheEngine()
		{
			var map = Folder("map", "a-map.pk3", isMap: true);
			var baseGame = Folder("Q3Content", "pak0.pk3");
			// The map's common.shader is shadowed by Quake 3's, which is searched first
			CreateFile("map/scripts/common.shader", Shader("textures/common/x", "map.tga"));
			CreateFile("Q3Content/scripts/common.shader", Shader("textures/common/y", "base.tga"));
			// A shader both define in different files: the file searched first wins
			CreateFile("map/scripts/amap.shader", Shader("textures/base_wall/shared", "map.tga") + Shader("textures/amap/own", "own.tga"));
			CreateFile("Q3Content/scripts/base_wall.shader", Shader("textures/base_wall/shared", "base.tga"));
			// Within a file, the last definition wins
			CreateFile("Q3Content/scripts/twice.shader", Shader("textures/twice/t", "first.tga") + Shader("Textures/Twice/T", "last.tga"));

			var searchPath = new Q3SearchPath(map, baseGame);
			var files = searchPath.GetShaderFiles().Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));
			Assert.That(files, Is.EqualTo(new[] { "Q3Content/scripts/base_wall.shader", "Q3Content/scripts/common.shader", "Q3Content/scripts/twice.shader", "map/scripts/amap.shader" }));

			var shaders = new ShaderLoader(searchPath.GetShaderFiles()).LoadShaders();
			Assert.That(shaders.ContainsKey("textures/common/x"), Is.False);
			Assert.That(shaders["textures/base_wall/shared"].stages[0].bundles[0].images[0], Is.EqualTo("base.tga"));
			Assert.That(shaders["TEXTURES/twice/t"].stages[0].bundles[0].images[0], Is.EqualTo("last.tga"));
			Assert.That(shaders.ContainsKey("textures/amap/own"), Is.True);

			// Named after pak0.pk3, the map's files win instead
			var after = new Q3SearchPath(Folder("map2", "z-map.pk3", isMap: true), baseGame);
			CreateFile("map2/scripts/amap.shader", Shader("textures/base_wall/shared", "map.tga"));
			var overridden = new ShaderLoader(after.GetShaderFiles()).LoadShaders();
			Assert.That(overridden["textures/base_wall/shared"].stages[0].bundles[0].images[0], Is.EqualTo("map.tga"));
		}

		[Test]
		public void DetectsTheModAMapNeeds()
		{
			var map = Folder("map", "mpteam1.pk3", isMap: true);
			var baseGame = Folder("Q3Content", "pak0.pk3");
			CreateFile("map/textures/mymap/own.tga");
			CreateFile("Q3Content/textures/base_wall/a.tga");
			CreateFile("Q3Mods/missionpack/textures/ctf2/redteam01.tga");
			CreateFile("Q3Mods/missionpack/scripts/ctf2.shader", Shader("textures/ctf2/blueteam01", "textures/ctf2/blueteam01.tga"));
			CreateFile("Q3Mods/other/textures/ctf2/redteam01.tga");
			CreateFile("Q3Mods/empty/.keep");
			File.Delete(Path.Combine(root, "Q3Mods", "empty", ".keep"));

			var modsDir = Path.Combine(root, "Q3Mods");
			Assert.That(Q3Mods.List(modsDir), Is.EqualTo(new[] { "missionpack", "other" }));
			var mods = Q3Mods.List(modsDir).Select(mod => Q3Mods.GetContentFolder(modsDir, mod)).ToList();
			var basePath = new Q3SearchPath(map, baseGame);

			var detection = Q3Mods.Detect(new[] { "textures/mymap/own", "textures/base_wall/a", "textures/ctf2/redteam01", "TEXTURES/ctf2/blueteam01", "textures/elsewhere/x" }, basePath, mods);
			Assert.That(detection.Mod?.Directory, Is.EqualTo(Path.Combine(modsDir, "missionpack")));
			Assert.That(detection.TexturesOnlyInMod, Is.EqualTo(new[] { "textures/ctf2/blueteam01", "textures/ctf2/redteam01" }));
			Assert.That(detection.Missing, Is.EqualTo(new[] { "textures/elsewhere/x" }));

			// Nothing only a mod has: Quake 3 alone
			var quake3 = Q3Mods.Detect(new[] { "textures/mymap/own", "textures/base_wall/a" }, basePath, mods);
			Assert.That(quake3.Mod, Is.Null);
			Assert.That(quake3.Missing, Is.Empty);

			Assert.That(Q3Mods.GetDisplayName("MissionPack"), Is.EqualTo("Team Arena"));
			Assert.That(Q3Mods.GetDisplayName("cpma"), Is.EqualTo("cpma"));
		}
	}
}
