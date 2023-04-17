using BSPConvertLib;

namespace BSPConvertCmd
{
	public class ConsoleLogger : ILogger
	{
		public void Log(string message)
		{
			Console.WriteLine(message);
		}
	}
}
