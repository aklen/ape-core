namespace Ape.Core.Logging;

/// <summary>
/// Simple logger interface for ApeCore.
/// </summary>
public interface ILogger
{
    void LogDebug(string message);
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
}
