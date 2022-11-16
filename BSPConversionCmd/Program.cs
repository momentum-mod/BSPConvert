using BSPConversionLib;
using CommandLine;

namespace BSPConversionCmd
{
	internal class Program
	{
		class Options
		{
			[Option("nopak", Required = false, HelpText = "Export models/textures into folders instead of embedding them in the BSP.")]
			public bool NoPak { get; set; }

			[Option("skyfix", Required = false, HelpText = "This is a temporary hack to fix skybox rendering.")]
			public bool SkyFix { get; set; }

			[Option("subdiv", Required = false, Default = 4, HelpText = "Displacement subdivisions [2-4].")]
			public int DisplacementPower { get; set; }

			[Option("newbsp", Required = false, HelpText = "Use BSP version 25 (Momentum Mod).")]
			public bool NewBSP { get; set; }

			[Value(0, MetaName = "input file", HelpText = "Input Quake 3 BSP/PK3 to be converted.", Required = true)]
			public string InputFile { get; set; }

			[Value(1, MetaName = "output directory", HelpText = "Output game directory for converted BSP/materials.", Required = true)]
			public string OutputDirectory { get; set; }
		}

		static void Main(string[] args)
		{
			//args = new string[3];
			//args[0] = @"c:\users\tyler\documents\tools\source engine\bspconvert\pornstar-sleeprun.pk3";
			//args[1] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";
			//args[2] = "--nopak";

			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(options =>
				{
					if (options.DisplacementPower < 2 || options.DisplacementPower > 4)
						throw new ArgumentOutOfRangeException("Displacement power must be between 2 and 4.");

					var converterOptions = new BSPConverterOptions()
					{
						noPak = options.NoPak,
						skyFix = options.SkyFix,
						DisplacementPower = options.DisplacementPower,
						newBSP = options.NewBSP,
						inputFile = options.InputFile,
						outputDir = options.OutputDirectory
					};
					var converter = new BSPConverter(converterOptions, new ConsoleLogger());
					converter.Convert();
				});
		}
	}
}