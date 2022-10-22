using BSPConversionLib;
using CommandLine;

namespace BSPConversionCmd
{
	internal class Program
	{
		class Options
		{
			[Option('m', "momentum", Required = false, HelpText = "Converts into Momentum Mod's custom BSP format.")]
			public bool MomentumConvert { get; set; }

			[Value(0, MetaName = "input file", HelpText = "Input Quake 3 BSP/PK3 to be converted.", Required = true)]
			public string InputFile { get; set; }

			[Value(1, MetaName = "output directory", HelpText = "Output game directory for converted BSP/materials.", Required = true)]
			public string OutputDirectory { get; set; }
		}

		static void Main(string[] args)
		{
			//args = new string[3];
			//args[0] = "-m";
			//args[1] = @"c:\users\tyler\documents\tools\source engine\bspconvert\ghost-strafe7.pk3";
			//args[2] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";

			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(options =>
				{
					var converter = new BSPConverter(options.InputFile, options.OutputDirectory, options.MomentumConvert, new ConsoleLogger());
					converter.Convert();
				});
		}
	}
}