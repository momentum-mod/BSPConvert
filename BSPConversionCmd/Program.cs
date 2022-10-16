using BSPConversionLib;

namespace BSPConversionCmd
{
	internal class Program
	{
		static void Main(string[] args)
		{
			//args = new string[3];
			//args[0] = @"C:\Users\tyler\Documents\Tools\Source Engine\BSPConvert\ghost-strafe7.pk3";
			//args[1] = @"C:\Users\tyler\Documents\Tools\Source Engine\BSPConvert\room-cube-source.bsp";
			//args[2] = @"C:\Users\tyler\Documents\Tools\Source Engine\BSPConvert\output";
			
			if (args.Length < 3)
			{
				Console.WriteLine("Usage: bspconv.exe <quake 3 bsp/pk3 file> <source bsp file> <game directory>");
				return;
			}

			var converter = new BSPConverter(args[0], args[1], args[2], new ConsoleLogger());
			converter.Convert();
		}
	}
}