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

            [Value(0, MetaName = "input file", HelpText = "Input Quake 3 BSP/PK3 file and its path to be converted. (i.e. C:\\path\\to\\file.pk3)", Required = true)]
            public string InputFile { get; set; }

            [Value(1, MetaName = "output directory", HelpText = "Output game directory for converted BSP/materials. (i.e. C:\\path\\to\\output\\folder)", Required = false)]
            public string OutputDirectory { get; set; }
        }

		static void Main(string[] args)
		{
            //args = new string[3];
            //args[0] = @"c:\users\tyler\documents\tools\source engine\bspconvert\kopo_fe.pk3";
            //args[1] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";
            //args[2] = "--newbsp";

            string[] inputFilePaths = new string[] { };
            List<string> inputEntries = new List<string>();
            foreach (var entry in args)
            {
                //Console.WriteLine(entry);
                if (Path.HasExtension(entry))
                {
                    inputEntries.Add(entry);
                    inputFilePaths = inputEntries.ToArray();
                }
            }
            string[] splitArgs = (String.Join(",", inputFilePaths).Split(','));

            Parser.Default.ParseArguments<Options>(args)
            .WithParsed(options =>
            {
                if (options.DisplacementPower < 2 || options.DisplacementPower > 4)
                    throw new ArgumentOutOfRangeException("Displacement power must be between 2 and 4.");

                if (splitArgs.Length > 0)
                    Console.WriteLine(@"Converting... (may take more than a few seconds)");

                foreach (var entry in splitArgs)
                {
                    options.InputFile = entry;
                    var converterOptions = new BSPConverterOptions()
                    {
                        noPak = options.NoPak,
                        skyFix = options.SkyFix,
                        DisplacementPower = options.DisplacementPower,
                        newBSP = options.NewBSP,
                        prefix = options.Prefix,
                        inputFile = options.InputFile,
                        outputDir = options.OutputDirectory
                    };
                    var converter = new BSPConverter(converterOptions, new ConsoleLogger());
                    converter.Convert();
                }

                Console.WriteLine($"\n" + ((splitArgs.Length > 0) ? "Convert Finished! " : "") + "Press any key to Exit.");
                Console.ReadKey();
            });
        }
	}
}