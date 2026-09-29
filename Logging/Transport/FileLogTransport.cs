using Ape.Core.Logging;

namespace Ape.Core.Logging.Transport;

/// <summary>
/// File log transport - writes logs to a file.
/// </summary>
public class FileLogTransport : ILogTransport
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public FileLogTransport(string logFilePath)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Open file for appending, auto-flush enabled
        _writer = new StreamWriter(logFilePath, append: true)
        {
            AutoFlush = true
        };
        
        // Write session start marker
        _writer.WriteLine();
        _writer.WriteLine($"=== Log session started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        _writer.WriteLine();
    }

    public void LogDebug(string message)
    {
        Log("DEBUG", message);
    }

    public void LogInfo(string message)
    {
        Log("INFO", message);
    }

    public void LogWarning(string message)
    {
        Log("WARN", message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        Log("ERROR", message);
        if (ex != null)
        {
            Log("ERROR", $"Exception: {ex}");
        }
    }

    private void Log(string level, string message)
    {
        lock (_lock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;
            _writer.WriteLine($"[{timestamp}] [{level,-5}] [T:{threadId,2}] {message}");
        }
    }

    ~FileLogTransport()
    {
        Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.WriteLine();
            _writer?.WriteLine($"=== Log session ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            _writer?.WriteLine();
            _writer?.Dispose();
        }
    }
}
