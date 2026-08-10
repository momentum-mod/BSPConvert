using BSPConvert.Lib;
using CommandLine;
using CommandLine.Text;

namespace BSPConvert.Cmd
{
	internal class Program
	{
		class Options
		{
			[Option("nopak", Required = false, HelpText = "Export materials into folders instead of embedding them in the BSP.")]
			public bool NoPak { get; set; }

			[Option("notooldisps", Required = false, HelpText = "Skip converting patches with tool textures to displacements.")]
			public bool NoToolDisplacements { get; set; }

			[Option("patchdisps", Required = false, HelpText = "Convert patches to displacements instead of primitive meshes (curved collision, no per-vertex UV/lightmap, not entity-attachable).")]
			public bool PatchesAsDisplacements { get; set; }

			[Option("subdiv", Required = false, Default = 3, HelpText = "Displacement subdivisions [2-4].")]
			public int DisplacementPower { get; set; }

			[Option("mindmg", Required = false, Default = 50, HelpText = "Minimum damage for trigger_hurt to respawn player.")]
			public int MinDamageToRespawnPlayer { get; set; }

			[Option("lavatriggers", Required = false, HelpText = "Duplicate Quake 3 lava brushes into trigger_hurt volumes that kill/respawn the player on contact.")]
			public bool LavaTriggers { get; set; }

			[Option("fogobb", Required = false, HelpText = "Use legacy obb_volumefog entities instead of the default Fog shader. Handles arbitrary brush shapes but can flicker on thin volumes.")]
			public bool UseObbFog { get; set; }

			[Option("fogminheight", Required = false, Default = 256f, HelpText = "Minimum vertical height (units) for converted fog volumes. Thin fog layers are expanded downward to this height so they span enough view froxels to reduce flickering. Only used with --fogobb.")]
			public float FogMinHeight { get; set; }

			[Option("nofogoverlay", Required = false, HelpText = "Don't draw fog shader visible stages (e.g. scrolling clouds) as an overlay face; fog brush faces are just dropped.")]
			public bool NoFogOverlay { get; set; }

			[Option("nozones", Required = false, HelpText = "Ignore timer zone triggers.")]
			public bool IgnoreZones { get; set; }

			[Option("noenvmap", Required = false, HelpText = "Disable envmap (specular cubemap) shader conversion. Useful for maps where Source's cubemap poorly emulates Quake 3's spheremap effect.")]
			public bool NoEnvMap { get; set; }

			[Option("clampoverbright", Required = false, HelpText = "Apply Quake 3's hue-preserving overbright clamp to lightmaps, flattening over-bright highlights toward white instead of letting the engine's 4x overbright blow past white.")]
			public bool ClampOverbright { get; set; }

			//[Option("oldbsp", Required = false, HelpText = "Use BSP version 20 (HL2 / CS:S).")]
			//public bool OldBSP { get; set; }

			[Option("noanim", Required = false, HelpText = "Disable baking Q3 multi-pass / animated shaders (scrolling liquids, layered effects, animMaps) into animated flipbook textures.")]
			public bool NoAnim { get; set; }

			[Option("animbudget", Required = false, Default = 16.0f, HelpText = "Target max size (MB) of each baked animated VTF. The master quality knob: resolution adapts down as the seamless loop needs more frames so total size stays near this (mips add ~33% on top). Higher = sharper/longer loops but larger files.")]
			public float AnimBudgetMB { get; set; }

			[Option("animmaxres", Required = false, Default = 512, HelpText = "Sharpness ceiling (px) for baked animated textures. The bake never exceeds this resolution even when the size budget would allow it.")]
			public int AnimMaxRes { get; set; }

			[Option("animfps", Required = false, Default = 16, HelpText = "Playback fps (smoothness) of baked animated textures. When the seamless loop needs more than --animmaxframes at this fps, the animation is sped up to fit rather than dropped below this fps.")]
			public int AnimFps { get; set; }

			[Option("animmaxframes", Required = false, Default = 512, HelpText = "Hard cap on baked frame count. When the loop's true length exceeds this at --animfps, the animation is compressed (plays faster) while staying seamless.")]
			public int AnimMaxFrames { get; set; }

			[Option("animalpha", Required = false, Default = 1.0f, HelpText = "Translucency [0-1] for baked liquids. 1 (default) derives per-texel translucency from each texture's alpha/luminance; below 1 uses a flat constant alpha (lower = more see-through).")]
			public float AnimAlpha { get; set; }

			[Option("prefix", Required = false, Default = "df_", HelpText = "Prefix for the converted BSP's file name.")]
			public string Prefix { get; set; }

			[Option("output", Required = false, HelpText = "Output game directory for converted BSP/materials.")]
			public string OutputDirectory { get; set; }

			[Option("maps", Required = false, Separator = ',', HelpText = "Convert only the named BSP(s) from a pk3 instead of every BSP it contains. Comma-separated map names without extension (e.g. --maps pgrocket,pgplasma). Case-insensitive.")]
			public IEnumerable<string> Maps { get; set; }

			[Option("offmodeents", Required = false, Default = "cpm", HelpText = "For maps with different cpm/vq3 entities, choose which entities to use when played in non-defrag modes (e.g. --offmodeents vq3).")]
			public string OffModeEntityFallback { get; set; }

			[Value(0, MetaName = "input files", Required = true, HelpText = "Input Quake 3 BSP/PK3 file(s) to be converted.")]
			public IEnumerable<string> InputFiles { get; set; }
		}

		static void Main(string[] args)
		{
#if DEBUG
			//args = new string[]
			//{
			//	@"c:\users\tyler\documents\tools\source engine\bspconvert\dfwc2017-6.pk3",
			//	"--output", @"c:\users\tyler\documents\tools\source engine\bspconvert\output",
			//};
#endif

			var parser = new Parser(with =>
			{
				with.HelpWriter = null;
				with.CaseInsensitiveEnumValues = true;
			});
			var parserResult = parser.ParseArguments<Options>(args);
			parserResult
				.WithParsed(options => RunCommand(options))
				.WithNotParsed(errors => DisplayHelp(errors, parserResult));
		}

		static void RunCommand(Options options)
		{
			if (options.DisplacementPower < 2 || options.DisplacementPower > 4)
				throw new ArgumentOutOfRangeException("Displacement power must be between 2 and 4.");

			if (options.OutputDirectory == null)
				options.OutputDirectory = Path.GetDirectoryName(options.InputFiles.First());

			if (options.OffModeEntityFallback != "cpm" && options.OffModeEntityFallback != "vq3")
				throw new ArgumentOutOfRangeException("Default entity state must be either 'cpm' or 'vq3'.");

			foreach (var inputEntry in options.InputFiles)
			{
				var converterOptions = new BSPConverterOptions()
				{
					noPak = options.NoPak,
					noToolDisplacements = options.NoToolDisplacements,
					patchesAsPrimitives = !options.PatchesAsDisplacements,
					DisplacementPower = options.DisplacementPower,
					minDamageToRespawnPlayer = options.MinDamageToRespawnPlayer,
					lavaTriggers = options.LavaTriggers,
					useObbFog = options.UseObbFog,
					fogMinHeight = options.FogMinHeight,
					noFogOverlay = options.NoFogOverlay,
					ignoreZones = options.IgnoreZones,
					noEnvMap = options.NoEnvMap,
					//oldBSP = options.OldBSP,
					prefix = options.Prefix,
					inputFile = inputEntry,
					outputDir = options.OutputDirectory,
					mapFilter = options.Maps?.ToArray(),
					offModeEntityFallback = options.OffModeEntityFallback,
					clampOverbright = options.ClampOverbright,
					flipbook = new FlipbookOptions()
					{
						enabled = !options.NoAnim,
						byteBudget = (long)(options.AnimBudgetMB * 1024 * 1024),
						maxResolution = options.AnimMaxRes,
						fps = options.AnimFps,
						maxFrames = options.AnimMaxFrames,
						alpha = options.AnimAlpha,
						autoAlpha = options.AnimAlpha >= 1f
					}
				};
				var converter = new BSPConverter(converterOptions, new ConsoleLogger());
				converter.Convert();
			}
		}

		static void DisplayHelp(IEnumerable<Error> errors, ParserResult<Options> parserResult)
		{
			const string version = "BSP Convert 0.0.3-alpha";
			if (errors.IsVersion())
			{
				Console.WriteLine(version);
				return;
			}
			
			var helpText = HelpText.AutoBuild(parserResult, h =>
			{
				h.AdditionalNewLineAfterOption = false;
				h.MaximumDisplayWidth = 400;
				h.Heading = version;
				h.Copyright = "";
				h.AddPostOptionsLine("EXAMPLE:\n  .\\BSPConv.exe \"C:\\Users\\<username>\\Documents\\BSPConvert\\nood-aDr.pk3\" --output \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Momentum Mod Playtest\\momentum\" --prefix \"df_\"");

				return HelpText.DefaultParsingErrorsHandler(parserResult, h);
			}, e => e);
			Console.WriteLine(helpText);
		}
	}
}
