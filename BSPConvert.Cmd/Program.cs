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

			[Option("patchprims", Required = false, HelpText = "Convert patches to primitive meshes instead of displacements (exact per-vertex UV/lightmap, entity-attachable, no curved collision).")]
			public bool PatchesAsPrimitives { get; set; }

			[Option("subdiv", Required = false, Default = 3, HelpText = "Displacement subdivisions [2-4].")]
			public int DisplacementPower { get; set; }

			[Option("mindmg", Required = false, Default = 50, HelpText = "Minimum damage for trigger_hurt to respawn player.")]
			public int MinDamageToRespawnPlayer { get; set; }

			[Option("nozones", Required = false, HelpText = "Ignore timer zone triggers.")]
			public bool IgnoreZones { get; set; }

			[Option("noenvmap", Required = false, HelpText = "Disable envmap (specular cubemap) shader conversion. Useful for maps where Source's cubemap poorly emulates Quake 3's spheremap effect.")]
			public bool NoEnvMap { get; set; }
			
			[Option("lightmapmin", Required = false, Default = 0.05f, HelpText = "Lightmap black-point lift in [0,1): remaps the [0,1] tonal range to [min,1], raising the darkest luxels off pure black to smooth out harsh/banded shadows (at the cost of shadow depth).")]
			public float LightmapMinBrightness { get; set; }

			[Option("clampoverbright", Required = false, HelpText = "Apply Quake 3's hue-preserving overbright clamp to lightmaps, flattening over-bright highlights toward white instead of letting the engine's 4x overbright blow past white.")]
			public bool ClampOverbright { get; set; }

			//[Option("oldbsp", Required = false, HelpText = "Use BSP version 20 (HL2 / CS:S).")]
			//public bool OldBSP { get; set; }

			[Option("prefix", Required = false, Default = "df_", HelpText = "Prefix for the converted BSP's file name.")]
			public string Prefix { get; set; }

			[Option("output", Required = false, HelpText = "Output game directory for converted BSP/materials.")]
			public string OutputDirectory { get; set; }

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

			var parser = new Parser(with => with.HelpWriter = null);
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

			foreach (var inputEntry in options.InputFiles)
			{
				var converterOptions = new BSPConverterOptions()
				{
					noPak = options.NoPak,
					noToolDisplacements = options.NoToolDisplacements,
					patchesAsPrimitives = options.PatchesAsPrimitives,
					DisplacementPower = options.DisplacementPower,
					minDamageToRespawnPlayer = options.MinDamageToRespawnPlayer,
					ignoreZones = options.IgnoreZones,
					noEnvMap = options.NoEnvMap,
					//oldBSP = options.OldBSP,
					prefix = options.Prefix,
					inputFile = inputEntry,
					outputDir = options.OutputDirectory,
					lightmapMinBrightness = options.LightmapMinBrightness,
					clampOverbright = options.ClampOverbright
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
