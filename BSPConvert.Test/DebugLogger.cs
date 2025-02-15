namespace BSPConvert.Test;

using System.Diagnostics;
using BSPConvert.Lib;

public class DebugLogger : ILogger
{
    public void Log(string message) => Debug.WriteLine(message);
}
