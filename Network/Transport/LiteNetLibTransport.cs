using Ape.Core.Logging;
using Ape.Core.Runtime.Service;
using Ape.Core.Network;
using Ape.Core.Replication;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Ape.Core.Network.Transport;

/// <summary>
/// LiteNetLib-based network transport implementation.
/// Provides UDP communication with reliable and unreliable channels.
/// </summary>
public class LiteNetLibTransport : INetworkTransport, INetEventListener
{
    private string _transportType = "litenetlib";

    private readonly ILogger _logger;
    private readonly NetManager _netManager;
    private readonly Dictionary<string, NetPeer> _peers = new();
    private bool _isRunning;
    private bool _isServer;
    private string _localPeerId = string.Empty;  // Empty = not connected/started

    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<string, byte[], int>? OnDataReceived;
    
    // Large File Transfer events
    public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
    public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
    public event Action<string, Guid, int>? OnFileChunkRequested;

    /// <summary>
    /// Local peer ID. Unique GUID-based identifier for ownership tracking.
    /// </summary>
    public string LocalPeerId => _localPeerId;

    /// <summary>
    /// Type of the transport (e.g., "lite-net-lib", "quic", "webrtc").
    /// </summary>
    public string TransportType => _transportType;

    public LiteNetLibTransport(ILogger logger)
    {
        _logger = logger;
        _netManager = new NetManager(this);
    }

    public void StartServer(int port, string? peerName = null, string? role = null)
    {
        if (_isRunning)
        {
            _logger.LogWarning("[LiteNetLibTransport] Already running");
            return;
        }

        _netManager.Start(port);
        _isRunning = true;
        _isServer = true;
        
        // Generate unique PeerId using PeerIdGenerator
        _localPeerId = PeerIdGenerator.Generate(peerName, role ?? "server", transportId: 0);
        
        _logger.LogInfo($"[LiteNetLibTransport] Server started on port {port}");
        _logger.LogInfo($"[LiteNetLibTransport] 🆔 LocalPeerId = {_localPeerId}");
    }

    public void Connect(string host, int port, string? peerName = null, string? role = null)
    {
        if (!_isRunning)
        {
            _netManager.Start();
            _isRunning = true;
        }

        // Generate unique PeerId using PeerIdGenerator
        _localPeerId = PeerIdGenerator.Generate(peerName, role ?? "client", transportId: 0);
        
        _netManager.Connect(host, port, "ApeCore");
        _logger.LogInfo($"[LiteNetLibTransport] Connecting to {host}:{port}");
        _logger.LogInfo($"[LiteNetLibTransport] 🆔 LocalPeerId = {_localPeerId}");
    }

    public void SendReliable(string peerId, byte[] data)
    {
        if (!_isRunning)
            return;

        lock (_peers)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                var writer = new NetDataWriter();
                writer.Put((byte)0); // Normal data message type
                writer.Put(data);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    public void SendUnreliable(string peerId, byte[] data)
    {
        if (!_isRunning)
            return;

        lock (_peers)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                var writer = new NetDataWriter();
                writer.Put((byte)0); // Normal data message type
                writer.Put(data);
                peer.Send(writer, DeliveryMethod.Unreliable);
            }
        }
    }

    public void PollEvents()
    {
        if (_isRunning)
        {
            _netManager.PollEvents();
        }
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _netManager.Stop();
        _peers.Clear();
        _isRunning = false;
        
        _logger.LogInfo("[LiteNetLibTransport] Stopped");
    }

    public IEnumerable<string> GetConnectedPeers()
    {
        lock (_peers)
        {
            return _peers.Keys.ToArray();
        }
    }
    
    // ========== Large File Transfer Implementation ==========
    
    public void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data)
    {
        NetPeer? peer;
        lock (_peers)
        {
            if (!_peers.TryGetValue(peerId, out peer))
            {
                _logger.LogWarning($"[LiteNetLibTransport] SendFileChunk: Peer {peerId} not found");
                return;
            }
        }
        
        // Protocol: [MessageType:1][FileId:16][ChunkIndex:4][DataLength:4][Data:N]
        var writer = new NetDataWriter();
        writer.Put((byte)1); // FileChunk message type
        writer.Put(fileId.ToByteArray());
        writer.Put(chunkIndex);
        writer.Put(data.Length);
        writer.Put(data);
        
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
        _logger.LogDebug($"[LiteNetLibTransport] Sent file chunk: fileId={fileId}, chunkIndex={chunkIndex}, size={data.Length} bytes to peer {peerId}");
    }
    
    public void RequestFileManifest(string peerId, Guid fileId)
    {
        NetPeer? peer;
        lock (_peers)
        {
            if (!_peers.TryGetValue(peerId, out peer))
            {
                _logger.LogWarning($"[LiteNetLibTransport] RequestFileManifest: Peer {peerId} not found");
                return;
            }
        }
        
        // Protocol: [MessageType:1][FileId:16]
        var writer = new NetDataWriter();
        writer.Put((byte)2); // ManifestRequest message type
        writer.Put(fileId.ToByteArray());
        
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
        _logger.LogDebug($"[LiteNetLibTransport] Requested file manifest: fileId={fileId} from peer {peerId}");
    }
    
    public void RequestFileChunk(string peerId, Guid fileId, int chunkIndex)
    {
        NetPeer? peer;
        lock (_peers)
        {
            if (!_peers.TryGetValue(peerId, out peer))
            {
                _logger.LogWarning($"[LiteNetLibTransport] RequestFileChunk: Peer {peerId} not found");
                return;
            }
        }
        
        // Protocol: [MessageType:1][FileId:16][ChunkIndex:4]
        var writer = new NetDataWriter();
        writer.Put((byte)3); // ChunkRequest message type
        writer.Put(fileId.ToByteArray());
        writer.Put(chunkIndex);
        
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
        _logger.LogDebug($"[LiteNetLibTransport] Requested file chunk: fileId={fileId}, chunkIndex={chunkIndex} from peer {peerId}");
    }

    public void Dispose()
    {
        Stop();
    }

    // INetEventListener implementation
    void INetEventListener.OnPeerConnected(NetPeer peer)
    {
        var peerId = peer.Id.ToString();
        lock (_peers)
        {
            _peers[peerId] = peer;
        }
        
        // Log connection
        if (!_isServer)
        {
            _logger.LogInfo($"[LiteNetLibTransport] Connected to server (Server Peer ID: {peerId}, LocalPeerId: {_localPeerId})");
        }
        else
        {
            _logger.LogInfo($"[LiteNetLibTransport] Peer connected: {peerId} ({peer.Address})");
        }
        
        OnPeerConnected?.Invoke(peerId);
    }

    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        var peerId = peer.Id.ToString();
        lock (_peers)
        {
            _peers.Remove(peerId);
        }
        
        _logger.LogInfo($"[LiteNetLibTransport] Peer disconnected: {peerId} (reason: {disconnectInfo.Reason})");
        OnPeerDisconnected?.Invoke(peerId);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        // Read message type
        var messageType = reader.GetByte();
        
        var peerId = peer.Id.ToString();
        switch (messageType)
        {
            case 0: // Normal data message
                var data = reader.GetRemainingBytes();
                OnDataReceived?.Invoke(peerId, data, data.Length);
                break;
            
            case 1: // FileChunk message
                HandleFileChunkMessage(peerId, reader);
                break;
            
            case 2: // ManifestRequest message
                HandleManifestRequestMessage(peerId, reader);
                break;
            
            case 3: // ChunkRequest message
                HandleChunkRequestMessage(peerId, reader);
                break;
            
            case 4: // ManifestResponse message
                HandleManifestResponseMessage(peerId, reader);
                break;
            
            default:
                _logger.LogWarning($"[LiteNetLibTransport] Unknown message type: {messageType} from peer {peerId}");
                break;
        }
    }
    
    private void HandleFileChunkMessage(string peerId, NetPacketReader reader)
    {
        // Protocol: [FileId:16][ChunkIndex:4][DataLength:4][Data:N]
        var fileIdBytes = new byte[16];
        reader.GetBytes(fileIdBytes, 16);
        var fileId = new Guid(fileIdBytes);
        var chunkIndex = reader.GetInt();
        var dataLength = reader.GetInt();
        var data = new byte[dataLength];
        reader.GetBytes(data, dataLength);
        
        _logger.LogDebug($"[LiteNetLibTransport] Received file chunk: fileId={fileId}, chunkIndex={chunkIndex}, size={dataLength} bytes from peer {peerId}");
        OnFileChunkReceived?.Invoke(peerId, fileId, chunkIndex, data);
    }
    
    private void HandleManifestRequestMessage(string peerId, NetPacketReader reader)
    {
        // Protocol: [FileId:16]
        var fileIdBytes = new byte[16];
        reader.GetBytes(fileIdBytes, 16);
        var fileId = new Guid(fileIdBytes);
        
        _logger.LogDebug($"[LiteNetLibTransport] Received manifest request: fileId={fileId} from peer {peerId}");
        // Note: Manifest response will be sent by upper layer (e.g., LargeFileTransferService)
    }
    
    private void HandleChunkRequestMessage(string peerId, NetPacketReader reader)
    {
        // Protocol: [FileId:16][ChunkIndex:4]
        var fileIdBytes = new byte[16];
        reader.GetBytes(fileIdBytes, 16);
        var fileId = new Guid(fileIdBytes);
        var chunkIndex = reader.GetInt();
        
        _logger.LogDebug($"[LiteNetLibTransport] Received chunk request: fileId={fileId}, chunkIndex={chunkIndex} from peer {peerId}");
        OnFileChunkRequested?.Invoke(peerId, fileId, chunkIndex);
    }
    
    private void HandleManifestResponseMessage(string peerId, NetPacketReader reader)
    {
        // Protocol: [FileId:16][TotalChunks:4][TotalSize:8][ChunkHashCount:4][ChunkHashes:N*64]
        var fileIdBytes = new byte[16];
        reader.GetBytes(fileIdBytes, 16);
        var fileId = new Guid(fileIdBytes);
        var totalChunks = reader.GetInt();
        var totalSize = reader.GetLong();
        var chunkHashCount = reader.GetInt();
        
        var chunkHashes = new string[chunkHashCount];
        for (int i = 0; i < chunkHashCount; i++)
        {
            var hashBytes = new byte[64]; // SHA256 hex string = 64 chars
            reader.GetBytes(hashBytes, 64);
            chunkHashes[i] = System.Text.Encoding.UTF8.GetString(hashBytes);
        }
        
        _logger.LogDebug($"[LiteNetLibTransport] Received manifest response: fileId={fileId}, totalChunks={totalChunks}, totalSize={totalSize} from peer {peerId}");
        OnFileManifestReceived?.Invoke(peerId, fileId, totalChunks, totalSize, chunkHashes);
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        _logger.LogError($"[LiteNetLibTransport] Network error from {endPoint}: {socketError}");
    }

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Ignore unconnected messages for now
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Optional: track latency
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Auto-accept all connections
        request.Accept();
    }
}
