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
    private const byte BulkChannel = 1;
    private const int MaxBulkPacketBytes = 272 * 1024;
    private const int MaxBulkQueuedBytesPerPeer = 4 * 1024 * 1024;
    private const int MaxBulkQueuedPacketsPerPeer = 64;
    private const int BulkPacketsPerPoll = 4;
    private const int BulkBytesPerPoll = 272 * 1024;

    private string _transportType = "litenetlib";

    private readonly ILogger _logger;
    private readonly NetManager _netManager;
    private readonly Dictionary<string, NetPeer> _peers = new();
    private readonly Dictionary<string, BulkQueue> _bulkQueues = new(StringComparer.Ordinal);
    private bool _isRunning;
    private bool _isServer;
    private string _localPeerId = string.Empty;  // Empty = not connected/started
    private readonly int _defaultPingInterval;
    private readonly int _defaultDisconnectTimeout;

    private sealed class BulkQueue
    {
        public readonly Queue<byte[]> Packets = new();
        public int Bytes;
    }

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
        _netManager = new NetManager(this) { ChannelsCount = BulkChannel + 1 };
        _defaultPingInterval = _netManager.PingInterval;
        _defaultDisconnectTimeout = _netManager.DisconnectTimeout;
    }

    public void SetKeepAlive(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        if (interval == TimeSpan.Zero)
        {
            _netManager.PingInterval = _defaultPingInterval;
            _netManager.DisconnectTimeout = _defaultDisconnectTimeout;
            return;
        }

        _netManager.PingInterval = TransportKeepAlive.LiteNetPingMilliseconds(interval);
        _netManager.DisconnectTimeout = TransportKeepAlive.LiteNetDisconnectMilliseconds(interval, _defaultDisconnectTimeout);
        _logger.LogInfo($"[LiteNetLibTransport] Keep-alive every {interval.TotalSeconds:0}s");
    }

    internal int PingIntervalMilliseconds => _netManager.PingInterval;

    internal int DisconnectTimeoutMilliseconds => _netManager.DisconnectTimeout;

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
        SendReliable(peerId, data, null);
    }

    public void SendReliable(string peerId, byte[] data, Action<bool>? onFlushed)
    {
        if (!_isRunning)
        {
            onFlushed?.Invoke(false);
            return;
        }

        lock (_peers)
        {
            if (!_peers.TryGetValue(peerId, out var peer))
            {
                onFlushed?.Invoke(false);
                return;
            }

            var writer = new NetDataWriter();
            writer.Put((byte)0); // Normal data message type
            writer.Put(data);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        onFlushed?.Invoke(true);
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

    public bool TrySendBulk(string peerId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!_isRunning || data.Length > MaxBulkPacketBytes)
            return false;

        lock (_peers)
        {
            if (!_peers.ContainsKey(peerId))
                return false;
            if (!_bulkQueues.TryGetValue(peerId, out var queue))
            {
                queue = new BulkQueue();
                _bulkQueues.Add(peerId, queue);
            }
            if (queue.Packets.Count >= MaxBulkQueuedPacketsPerPeer
                || queue.Bytes > MaxBulkQueuedBytesPerPeer - data.Length)
                return false;
            queue.Packets.Enqueue(data.ToArray());
            queue.Bytes += data.Length;
            return true;
        }
    }

    public void PollEvents()
    {
        if (_isRunning)
        {
            _netManager.PollEvents();
            DrainBulkQueues();
        }
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _netManager.Stop();
        lock (_peers)
        {
            _peers.Clear();
            _bulkQueues.Clear();
        }
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
            _bulkQueues[peerId] = new BulkQueue();
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
            _bulkQueues.Remove(peerId);
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

    private void DrainBulkQueues()
    {
        lock (_peers)
        {
            foreach (var (peerId, queue) in _bulkQueues)
            {
                if (!_peers.TryGetValue(peerId, out var peer))
                    continue;

                var sentPackets = 0;
                var sentBytes = 0;
                while (queue.Packets.TryPeek(out var packet)
                    && sentPackets < BulkPacketsPerPoll
                    && (sentPackets == 0 || sentBytes <= BulkBytesPerPoll - packet.Length))
                {
                    try
                    {
                        var writer = new NetDataWriter();
                        writer.Put((byte)0);
                        writer.Put(packet);
                        peer.Send(writer, BulkChannel, DeliveryMethod.ReliableOrdered);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            $"[LiteNetLibTransport] Bulk send to peer {peerId} remains queued: {ex.Message}");
                        break;
                    }
                    queue.Packets.Dequeue();
                    queue.Bytes -= packet.Length;
                    sentPackets++;
                    sentBytes += packet.Length;
                }
            }
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
