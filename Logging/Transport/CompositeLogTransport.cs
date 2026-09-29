using Ape.Core.Logging;

namespace Ape.Core.Logging.Transport;

/// <summary>
/// Composite log transport - writes logs to multiple transports simultaneously.
/// </summary>
public class CompositeLogTransport : ILogTransport
{
    private readonly ILogTransport[] _transports;

    public CompositeLogTransport(params ILogTransport[] transports)
    {
        _transports = transports;
    }

    public void LogDebug(string message)
    {
        foreach (var transport in _transports)
        {
            transport.LogDebug(message);
        }
    }

    public void LogInfo(string message)
    {
        foreach (var transport in _transports)
        {
            transport.LogInfo(message);
        }
    }

    public void LogWarning(string message)
    {
        foreach (var transport in _transports)
        {
            transport.LogWarning(message);
        }
    }

    public void LogError(string message, Exception? ex = null)
    {
        foreach (var transport in _transports)
        {
            transport.LogError(message, ex);
        }
    }
}
