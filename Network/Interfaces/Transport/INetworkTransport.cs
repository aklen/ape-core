namespace Ape.Core.Network.Transport;

/// <summary>
/// Abstract transport layer for network communication.
/// Implementations can use UDP (LiteNetLib), QUIC, WebRTC, etc.
/// </summary>
public interface INetworkTransport : IDisposable
{
    /// <summary>
    /// Type of the transport (e.g., "udp", "quic", "webrtc").
    /// </summary>
    string TransportType { get; }

    /// <summary>
    /// Local peer ID for this transport instance.
    /// Unique identifier (GUID or transport-assigned). Used for ownership tracking.
    /// </summary>
    string LocalPeerId { get; }

    /// <summary>
    /// Event fired when a new peer connects (server-side) or when connection is established (client-side).
    /// </summary>
    event Action<string>? OnPeerConnected;

    /// <summary>
    /// Event fired when a peer disconnects.
    /// </summary>
    event Action<string>? OnPeerDisconnected;

    /// <summary>
    /// Event fired when data is received from a peer.
    /// </summary>
    event Action<string, byte[], int>? OnDataReceived;

    /// <summary>
    /// Start as server, listening for incoming connections.
    /// </summary>
    /// <param name="port">Port to listen on</param>
    /// <param name="peerName">Optional human-readable peer name (from config/CLI)</param>
    /// <param name="role">Optional network role ("server", "client", "peer")</param>
    void StartServer(int port, string? peerName = null, string? role = null);

    /// <summary>
    /// Connect to a server as a client.
    /// </summary>
    /// <param name="host">Server hostname or IP</param>
    /// <param name="port">Server port</param>
    /// <param name="peerName">Optional human-readable peer name (from config/CLI)</param>
    /// <param name="role">Optional network role ("server", "client", "peer")</param>
    void Connect(string host, int port, string? peerName = null, string? role = null);

    /// <summary>
    /// Send data reliably (guaranteed delivery, ordered).
    /// </summary>
    /// <param name="peerId">Target peer ID (string)</param>
    /// <param name="data">Data to send</param>
    void SendReliable(string peerId, byte[] data);

    /// <summary>
    /// Send data unreliably (best-effort, may be lost or out-of-order).
    /// </summary>
    /// <param name="peerId">Target peer ID (string)</param>
    /// <param name="data">Data to send</param>
    void SendUnreliable(string peerId, byte[] data);

    /// <summary>
    /// Process incoming/outgoing packets. Must be called regularly (e.g., in main loop).
    /// </summary>
    void PollEvents();

    /// <summary>
    /// Stop the transport (close all connections).
    /// </summary>
    void Stop();

    /// <summary>
    /// Get all connected peer IDs (string).
    /// </summary>
    IEnumerable<string> GetConnectedPeers();

    // ========== Large File Transfer (Chunk-based) ==========

    /// <summary>
    /// Send a file chunk to a peer.
    /// </summary>
    /// <param name="peerId">Target peer ID (string)</param>
    /// <param name="fileId">Unique file identifier (Guid)</param>
    /// <param name="chunkIndex">Zero-based chunk index</param>
    /// <param name="data">Chunk data</param>
    void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data);

    /// <summary>
    /// Event fired when a file chunk is received from a peer.
    /// Args: (peerId, fileId, chunkIndex, data)
    /// </summary>
    event Action<string, Guid, int, byte[]>? OnFileChunkReceived;

    /// <summary>
    /// Request file manifest (metadata) from a peer.
    /// </summary>
    /// <param name="peerId">Target peer ID (string)</param>
    /// <param name="fileId">File identifier to request</param>
    void RequestFileManifest(string peerId, Guid fileId);

    /// <summary>
    /// Event fired when a file manifest is received from a peer.
    /// Args: (peerId, fileId, totalChunks, totalSize, chunkHashes)
    /// </summary>
    event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;

    /// <summary>
    /// Request a specific chunk from a peer.
    /// </summary>
    /// <param name="peerId">Target peer ID (string)</param>
    /// <param name="fileId">File identifier</param>
    /// <param name="chunkIndex">Chunk index to request</param>
    void RequestFileChunk(string peerId, Guid fileId, int chunkIndex);

    /// <summary>
    /// Event fired when a chunk request is received.
    /// Args: (peerId, fileId, chunkIndex)
    /// </summary>
    event Action<string, Guid, int>? OnFileChunkRequested;
}
