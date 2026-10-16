namespace Ape.Core.Network.Transport;

/// <summary>
/// One-shot outbound dial that can run beside an open listener.
/// The session above this transport owns backoff and which endpoints stay desired.
/// </summary>
public interface IOutboundDial
{
    /// <summary>
    /// Prepare a client socket without dialing. A running listener is left as it is.
    /// </summary>
    void StartOutbound(string? peerName = null, string? role = null);

    /// <summary>
    /// Open one connection to <paramref name="host"/>:<paramref name="port"/>.
    /// Failure is <see cref="DialFailed"/>. Success is <see cref="OutboundConnected"/> after <c>OnPeerConnected</c>.
    /// </summary>
    void Dial(string host, int port);

    /// <summary>
    /// The outbound attempt reached a live connection. Args: connection id, host, port.
    /// </summary>
    event Action<string, string, int>? OutboundConnected;

    /// <summary>
    /// The outbound attempt ended before a connection existed. Args: host, port.
    /// </summary>
    event Action<string, int>? DialFailed;
}
