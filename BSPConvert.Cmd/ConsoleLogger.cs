using BSPConvert.Lib;

namespace BSPConvert.Cmd
{
	public class ConsoleLogger : ILogger
	{
		public void Log(string message)
		{
			Console.WriteLine(message);
		}
	}
}
