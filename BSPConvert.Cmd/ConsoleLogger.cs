namespace BSPConvert.Cmd;
using BSPConvert.Lib;

public class ConsoleLogger : ILogger
	{
    public void Log(string message) => Console.WriteLine(message);
}
