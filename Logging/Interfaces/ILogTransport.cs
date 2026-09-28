namespace Ape.Core.Logging;

/// <summary>
/// Interface for log transport implementations.
/// Transport determines WHERE logs are written (console, file, syslog, etc.)
/// </summary>
public interface ILogTransport
{
    /// <summary>Log a debug message.</summary>
    void LogDebug(string message);

    /// <summary>Log an informational message.</summary>
    void LogInfo(string message);

    /// <summary>Log a warning message.</summary>
    void LogWarning(string message);

    /// <summary>Log an error message with optional exception details.</summary>
    void LogError(string message, Exception? ex = null);
}
