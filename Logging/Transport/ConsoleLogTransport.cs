using Ape.Core.Logging;

namespace Ape.Core.Logging.Transport;

/// <summary>
/// Console log transport - writes logs to console with color coding.
/// </summary>
public class ConsoleLogTransport : ILogTransport
{
    private readonly object _lock = new();

    public void LogDebug(string message)
    {
        Log("DEBUG", message, ConsoleColor.Gray);
    }

    public void LogInfo(string message)
    {
        Log("INFO", message, ConsoleColor.White);
    }

    public void LogWarning(string message)
    {
        Log("WARN", message, ConsoleColor.Yellow);
    }

    public void LogError(string message, Exception? ex = null)
    {
        Log("ERROR", message, ConsoleColor.Red);
        if (ex != null)
        {
            Log("ERROR", ex.ToString(), ConsoleColor.DarkRed);
        }
    }

    private void Log(string level, string message, ConsoleColor color)
    {
        lock (_lock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;
            
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{timestamp}] ");
            
            Console.ForegroundColor = color;
            Console.Write($"[{level,-5}] ");
            
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"[T:{threadId,2}] ");
            
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            
            Console.ResetColor();
        }
    }
}
