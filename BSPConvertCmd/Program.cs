using BSPConvertLib;
using CommandLine;
using CommandLine.Text;

namespace BSPConvertCmd
{
	internal class Program
	{
		class Options
		{
			[Option("nopak", Required = false, HelpText = "Export materials into folders instead of embedding them in the BSP.")]
			public bool NoPak { get; set; }

			[Option("subdiv", Required = false, Default = 4, HelpText = "Displacement subdivisions [2-4].")]
			public int DisplacementPower { get; set; }

			[Option("mindmg", Required = false, Default = 50, HelpText = "Minimum damage to convert trigger_hurt into trigger_teleport.")]
			public int MinDamageToConvertTrigger { get; set; }

			//[Option("oldbsp", Required = false, HelpText = "Use BSP version 20 (HL:2 / CS:S).")]
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
			//args = new string[]
			//{
			//	@"c:\users\tyler\documents\tools\source engine\bspconvert\dfwc2017-6.pk3",
			//	"--output", @"c:\users\tyler\documents\tools\source engine\bspconvert\output",
			//};

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
					DisplacementPower = options.DisplacementPower,
					minDamageToConvertTrigger = options.MinDamageToConvertTrigger,
					//oldBSP = options.OldBSP,
					prefix = options.Prefix,
					inputFile = inputEntry,
					outputDir = options.OutputDirectory
				};
				var converter = new BSPConverter(converterOptions, new ConsoleLogger());
				converter.Convert();
			}
		}

		static void DisplayHelp(IEnumerable<Error> errors, ParserResult<Options> parserResult)
		{
			const string version = "BSP Convert 0.0.1-alpha";
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