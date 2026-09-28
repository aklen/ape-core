using Ape.Core.Logging;

namespace Ape.Core.Logging.Transport;

/// <summary>
/// No-op log transport — discards all log output. Use when <c>transport</c> is <c>none</c>.
/// </summary>
public sealed class NullLogTransport : ILogTransport
{
    public void LogDebug(string message)
    {
    }

    public void LogInfo(string message)
    {
    }

    public void LogWarning(string message)
    {
    }

    public void LogError(string message, Exception? ex = null)
    {
    }
}
