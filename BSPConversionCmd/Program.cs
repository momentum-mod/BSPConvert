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

			[Option("prefix", Required = false, HelpText = "Prefix for the converted BSP's file name.")]
			public string Prefix { get; set; }

			[Value(0, MetaName = "input files", Required = true, HelpText = "Input file(s) and its path to be processed. (i.e. \"C:\\path\\to\\file.pk3)\" ")]
			public IEnumerable<string> InputFile { get; set; }

			[Option('o', "output", Required = false, HelpText = "Output game directory for converted BSP/materials. (i.e. -output \"C:\\path\\to\\output\\folder)\" ")]
			public string OutputDirectory { get; set; }
		}

		static void Main(string[] args)
		{
			//args = new string[3];
			//args[0] = @"c:\users\tyler\documents\tools\source engine\bspconvert\kopo_fe.pk3";
			//args[1] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";
			//args[2] = "--newbsp";

			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(options =>
				{
					if (options.DisplacementPower < 2 || options.DisplacementPower > 4)
						throw new ArgumentOutOfRangeException("Displacement power must be between 2 and 4.");

					if (options.InputFile.Count() > 0)
						Console.WriteLine(@"Converting... (may take more than a few seconds)");

					foreach (var inputEntry in options.InputFile)
					{

						if (options.OutputDirectory == null)
							options.OutputDirectory = Path.GetDirectoryName(inputEntry);

						var converterOptions = new BSPConverterOptions()
						{
							noPak = options.NoPak,
							skyFix = options.SkyFix,
							DisplacementPower = options.DisplacementPower,
							newBSP = options.NewBSP,
							prefix = options.Prefix,
							inputFile = inputEntry,
							outputDir = options.OutputDirectory
						};
						var converter = new BSPConverter(converterOptions, new ConsoleLogger());
						converter.Convert();
					}
					// Console.ReadKey();
				});
		}
	}
}