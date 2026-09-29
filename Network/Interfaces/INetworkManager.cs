namespace Ape.Core.Network;

/// <summary>
/// Network transport layer for ApeCore.
/// Provides RakNet-like UDP functionality with reliable/unreliable channels.
/// </summary>
public interface INetworkManager
{
    /// <summary>
    /// Local peer ID for this instance.
    /// Unique identifier for this peer in the network. Can be a GUID, or assigned by transport (e.g., RakNet PeerID).
    /// Used for replica ownership tracking. Server generates this on startup, clients may receive from STUN server.
    /// </summary>
    string LocalPeerId { get; }

    /// <summary>
    /// Network role of this instance ("server", "client", "peer").
    /// </summary>
    string Role { get; }

    /// <summary>
    /// Start the network manager as a server.
    /// </summary>
    /// <param name="port">Port to listen on</param>
    /// <param name="peerName">Optional human-readable peer name (from config/CLI)</param>
    /// <param name="role">Optional network role ("server", "client", "peer")</param>
    void StartServer(int port, string? peerName = null, string? role = null);

    /// <summary>
    /// Connect to a server as a client.
    /// </summary>
    /// <param name="address">Server address</param>
    /// <param name="port">Server port</param>
    /// <param name="peerName">Optional human-readable peer name (from config/CLI)</param>
    /// <param name="role">Optional network role ("server", "client", "peer")</param>
    void Connect(string address, int port, string? peerName = null, string? role = null);

    /// <summary>
    /// Stop the network manager and disconnect all peers.
    /// </summary>
    void Stop();

    /// <summary>
    /// Send data to all connected peers.
    /// </summary>
    /// <param name="data">Data to send</param>
    /// <param name="reliable">Whether to use reliable delivery</param>
    void Broadcast(byte[] data, bool reliable = true);

    /// <summary>
    /// Send data to a specific peer.
    /// </summary>
    /// <param name="peerId">Target peer identifier (string)</param>
    /// <param name="data">Data to send</param>
    /// <param name="reliable">Whether to use reliable delivery</param>
    void Send(string peerId, byte[] data, bool reliable = true);

    /// <summary>
    /// Process network events. Should be called regularly.
    /// </summary>
    void PollEvents();

    /// <summary>
    /// Fired when data is received from any peer.
    /// Callback receives: (peerId, data)
    /// </summary>
    event Action<string, byte[]>? OnDataReceived;

    /// <summary>
    /// Fired when a peer connects.
    /// </summary>
    event Action<string>? OnPeerConnected;

    /// <summary>
    /// Fired when a peer disconnects.
    /// </summary>
    event Action<string>? OnPeerDisconnected;

    /// <summary>
    /// Get all currently connected peer IDs.
    /// </summary>
    IReadOnlyList<string> GetConnectedPeers();

    /// <summary>
    /// Get all local IPv4 addresses from network interfaces.
    /// Excludes loopback (127.0.0.1) and link-local addresses.
    /// </summary>
    /// <returns>List of local IP addresses</returns>
    IEnumerable<string> GetLocalIPAddresses();

    /// <summary>
    /// Get the preferred local IP address for network communication.
    /// Prefers non-loopback, non-link-local IPv4 addresses.
    /// Returns the first active ethernet/wifi adapter IP.
    /// </summary>
    /// <returns>Preferred IP address or "127.0.0.1" as fallback</returns>
    string GetPreferredLocalIPAddress();
}
