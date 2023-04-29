using BSPConvert.Lib;
using System.Diagnostics;

namespace BSPConvert.Test
{
	public class DebugLogger : ILogger
	{
		public void Log(string message)
		{
			Debug.WriteLine(message);
		}
	}
}
