using BSPConversionLib;
using CommandLine;

namespace BSPConversionCmd
{
	internal class Program
	{
		class ConvertOptions
		{
			[Option("nopak", Required = false, HelpText = "Export models/textures into folders instead of embedding them in the BSP.")]
			public bool NoPak { get; set; }

			[Option("skyfix", Required = false, HelpText = "This is a temporary hack to fix skybox rendering.")]
			public bool SkyFix { get; set; }

			[Value(0, MetaName = "input file", HelpText = "Input Quake 3 BSP/PK3 to be converted.", Required = true)]
			public string InputFile { get; set; }

			[Value(1, MetaName = "output directory", HelpText = "Output game directory for converted BSP/materials.", Required = true)]
			public string OutputDirectory { get; set; }
		}

		static void Main(string[] args)
		{
			//args = new string[3];
			//args[0] = @"c:\users\tyler\documents\tools\source engine\bspconvert\test-skybox.pk3";
			//args[1] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";
			//args[2] = "--nopak";

			Parser.Default.ParseArguments<ConvertOptions>(args)
				.WithParsed(options =>
				{
					var converter = new BSPConverter(options.InputFile, options.OutputDirectory, options.NoPak, options.SkyFix, new ConsoleLogger());
					converter.Convert();
				});
		}
	}
}