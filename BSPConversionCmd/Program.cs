using BSPConversionLib;

namespace BSPConversionCmd
{
	internal class Program
	{
		static void Main(string[] args)
		{
			//args = new string[3];
			//args[0] = @"c:\users\tyler\documents\tools\source engine\bspconvert\ghost-strafe7.pk3";
			//args[2] = @"c:\users\tyler\documents\tools\source engine\bspconvert\output";

			if (args.Length < 2)
			{
				Console.WriteLine("Usage: bspconv.exe <quake 3 bsp/pk3 file> <game directory>");
				return;
			}

			var converter = new BSPConverter(args[0], args[1], new ConsoleLogger());
			converter.Convert();
		}
	}
}