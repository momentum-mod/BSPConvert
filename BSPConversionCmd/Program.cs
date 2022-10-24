using BSPConversionLib;
using CommandLine;

namespace BSPConversionCmd
{
	internal class Program
	{
		class Options
		{
			[Value(0, MetaName = "input file", HelpText = "Input Quake 3 BSP/PK3 to be converted.", Required = true)]
			public string InputFile { get; set; }

			[Value(1, MetaName = "output directory", HelpText = "Output game directory for converted BSP/materials.", Required = true)]
			public string OutputDirectory { get; set; }
		}

		static void Main(string[] args)
		{
			//args = new string[2];
			//args[0] = @"c:\users\tyler\documents\tools\source engine\bspconvert\ghost-strafe7.pk3";
			//args[1] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";

			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(options =>
				{
					var converter = new BSPConverter(options.InputFile, options.OutputDirectory, new ConsoleLogger());
					converter.Convert();
				});
		}
	}
}