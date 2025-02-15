namespace BSPConvert.Test;
using BSPConvert.Lib;
using System.Diagnostics;

public class DebugLogger : ILogger
	{
    public void Log(string message) => Debug.WriteLine(message);
}
