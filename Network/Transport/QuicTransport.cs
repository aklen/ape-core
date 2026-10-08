using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Runtime.Service;
using Ape.Core.Replication;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Ape.Core.Network.Transport;

/// <summary>
/// QUIC-based network transport implementation (.NET 9+).
/// Provides TLS 1.3 encrypted communication with multiplexed streams.
/// <para>
/// Phase C: app data uses persistent unidirectional streams with length-prefixed
/// framing (not one stream per message). <see cref="SendReliable"/> and
/// <see cref="SendUnreliable"/> each keep a dedicated outbound stream per peer so
/// telemetry is not head-of-line blocked behind large media writes.
/// Native QUIC datagrams are not yet exposed by System.Net.Quic — unreliable
/// traffic still rides a reliable stream until that API lands.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class QuicTransport : INetworkTransport
{
    private const byte MessageTypeNormal = 0;
    private const byte MessageTypeFileChunk = 1;
    private const byte MessageTypeManifestRequest = 2;
    private const byte MessageTypeChunkRequest = 3;
    private const byte MessageTypeManifestResponse = 4;

    /// <summary>Upper bound for a single framed app-data payload (media keyframes can be multi‑MB).</summary>
    private const int MaxFramedMessageBytes = 64 * 1024 * 1024;

    private string _transportType = "quic";

    private readonly ILogger _logger;
    private QuicListener? _listener;
    private QuicConnection? _clientConnection;
    private readonly ConcurrentDictionary<string, QuicConnection> _connections = new();
    private readonly ConcurrentDictionary<string, PeerSendChannels> _peerSendChannels = new();
    private readonly CancellationTokenSource _cts = new();
    
    private int _nextPeerId = 1;
    private bool _isRunning;
    private string _localPeerId = string.Empty;  // Empty = not connected/started

    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<string, byte[], int>? OnDataReceived;
    
    // Large File Transfer events
    public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
    public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
    public event Action<string, Guid, int>? OnFileChunkRequested;

    /// <summary>Outbound persistent streams for one peer (reliable + unreliable planes).</summary>
    private sealed class PeerSendChannels
    {
        public readonly OutboundChannel Reliable = new("reliable");
        public readonly OutboundChannel Unreliable = new("unreliable");
    }

    private sealed class OutboundChannel(string name)
    {
        public readonly string Name = name;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public QuicStream? Stream;
    }

    /// <summary>
    /// Local peer ID. Unique GUID-based identifier for ownership tracking.
    /// </summary>
    public string LocalPeerId => _localPeerId;

    /// <summary>
    /// Type of the transport (e.g., "udp", "quic", "webrtc").
    /// </summary>
    public string TransportType => _transportType;

    public QuicTransport(ILogger logger)
    {
        _logger = logger;
        
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not supported on this platform. Requires .NET 9+ and QUIC support in OS.");
        }
    }

    public void StartServer(int port, string? peerName = null, string? role = null)
    {
        if (_isRunning)
        {
            _logger.LogWarning("[QuicTransport] Already running");
            return;
        }

        _isRunning = true;
        
        // Generate unique PeerId using PeerIdGenerator
        _localPeerId = PeerIdGenerator.Generate(peerName, role ?? "server", transportId: 0);

        // Create self-signed certificate for development
        var cert = CertificateHelper.GenerateSelfSignedCertificate("ApeCore");

        var alpnProtocol = new SslApplicationProtocol("apecore");
        _logger.LogInfo($"[QuicTransport] Server ALPN protocol: {System.Text.Encoding.UTF8.GetString(alpnProtocol.Protocol.ToArray())}");
        _logger.LogInfo($"[QuicTransport] Server certificate subject: {cert.Subject}, thumbprint: {cert.Thumbprint}");
        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
            ApplicationProtocols = new List<SslApplicationProtocol> { alpnProtocol },
            ConnectionOptionsCallback = (connection, sslInfo, cancellationToken) =>
            {
                _logger.LogInfo($"[QuicTransport] ConnectionOptionsCallback called, SNI: {sslInfo.ServerName}");
                return ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    MaxInboundBidirectionalStreams = 100, // Allow client to open streams to server
                    MaxInboundUnidirectionalStreams = 100,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = new List<SslApplicationProtocol> { alpnProtocol },
                        ServerCertificate = cert,
                        ClientCertificateRequired = false,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true // Accept all certs in dev mode
                    }
                });
            }
        };

        _listener = QuicListener.ListenAsync(listenerOptions).GetAwaiter().GetResult();
        _logger.LogInfo($"[QuicTransport] Server started on port {port} (QUIC/TLS 1.3)");
        _logger.LogInfo($"[QuicTransport] Listener LocalEndPoint: {_listener.LocalEndPoint}");
        _logger.LogInfo($"[QuicTransport] 🆔 LocalPeerId = {_localPeerId}");

        // Start accepting connections
        _ = Task.Run(AcceptConnectionsAsync);
    }

    public void Connect(string host, int port, string? peerName = null, string? role = null)
    {
        if (_isRunning)
        {
            _logger.LogWarning("[QuicTransport] Already running");
            return;
        }

        _isRunning = true;
        
        // Generate unique PeerId using PeerIdGenerator
        _localPeerId = PeerIdGenerator.Generate(peerName, role ?? "client", transportId: 0);

        _ = Task.Run(async () =>
        {
            try
            {
                var alpnProtocol = new SslApplicationProtocol("apecore");
                _logger.LogInfo($"[QuicTransport] Client ALPN protocol: {System.Text.Encoding.UTF8.GetString(alpnProtocol.Protocol.ToArray())}");
                _logger.LogInfo($"[QuicTransport] Connecting to {host}:{port} with TargetHost: {host}");
                _logger.LogInfo($"[QuicTransport] 🆔 LocalPeerId = {_localPeerId}");
                var clientOptions = new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new DnsEndPoint(host, port),
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    MaxInboundBidirectionalStreams = 100, // Allow server to open streams to client
                    MaxInboundUnidirectionalStreams = 100,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = new List<SslApplicationProtocol> { alpnProtocol },
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true, // Accept all certs in dev mode
                        TargetHost = host // Explicitly set TargetHost for SNI
                    }
                };

                _clientConnection = await QuicConnection.ConnectAsync(clientOptions, _cts.Token);
                var peerId = "0"; // Server is always peer "0" for client
                _connections[peerId] = _clientConnection;

                _logger.LogInfo($"[QuicTransport] Connected to {host}:{port}");
                OnPeerConnected?.Invoke(peerId);

                // Start receiving on reliable stream
                _ = Task.Run(() => ReceiveFromConnectionAsync(peerId, _clientConnection));
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QuicTransport] Connection failed: {ex.Message}", ex);
            }
        });
    }

    private async Task AcceptConnectionsAsync()
    {
        if (_listener == null)
            return;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var connection = await _listener.AcceptConnectionAsync(_cts.Token);
                var peerId = Interlocked.Increment(ref _nextPeerId).ToString();
                _connections[peerId] = connection;

                _logger.LogInfo($"[QuicTransport] Peer connected: {peerId} ({connection.RemoteEndPoint})");
                OnPeerConnected?.Invoke(peerId);

                // Start receiving from this connection
                _ = Task.Run(() => ReceiveFromConnectionAsync(peerId, connection));
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                // Failed handshake / aborted client must not kill the accept loop —
                // otherwise the server stops taking new peers until restart.
                _logger.LogWarning($"[QuicTransport] Accept/handshake failed (continuing): {ex.Message}");
            }
        }
    }

    private async Task ReceiveFromConnectionAsync(string peerId, QuicConnection connection)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested && connection != null)
            {
                var stream = await connection.AcceptInboundStreamAsync(_cts.Token);
                _ = Task.Run(() => ReceiveFromStreamAsync(peerId, stream));
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            _logger.LogInfo($"[QuicTransport] Peer {peerId} disconnected");
            HandlePeerDisconnect(peerId);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[QuicTransport] Receive error from peer {peerId}: {ex.Message}", ex);
            HandlePeerDisconnect(peerId);
        }
    }

    /// <summary>
    /// True for peer/local teardown that should not surface as ERROR (Ctrl+C, peer quit, idle close).
    /// </summary>
    private static bool IsExpectedDisconnect(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException!)
        {
            switch (cur)
            {
                case OperationCanceledException:
                case ObjectDisposedException:
                case EndOfStreamException:
                    return true;
                case QuicException qex:
                    switch (qex.QuicError)
                    {
                        case QuicError.Success:
                        case QuicError.ConnectionAborted:
                        case QuicError.StreamAborted:
                        case QuicError.ConnectionIdle:
                        case QuicError.OperationAborted:
                        case QuicError.ConnectionTimeout:
                            return true;
                        case QuicError.InternalError:
                            // MsQuic often reports clean peer/transport shutdown as
                            // InternalError + QUIC_STATUS_SUCCESS (seen on Android quit).
                            if (LooksLikeCleanQuicShutdown(qex.Message))
                                return true;
                            break;
                    }
                    // Fallback for older / odd message shapes
                    if (LooksLikeCleanQuicShutdown(qex.Message)
                        || qex.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase)
                        || qex.Message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase))
                        return true;
                    break;
            }
        }

        return false;
    }

    private static bool LooksLikeCleanQuicShutdown(string message) =>
        message.Contains("QUIC_STATUS_SUCCESS", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ShutdownInitiatedByTransport", StringComparison.OrdinalIgnoreCase);

    private void HandlePeerDisconnect(string peerId)
    {
        // Idempotent: only the first caller notifies upper layers.
        if (!_connections.TryRemove(peerId, out var connection))
        {
            DisposePeerSendChannels(peerId);
            return;
        }

        _ = connection.DisposeAsync();
        DisposePeerSendChannels(peerId);
        OnPeerDisconnected?.Invoke(peerId);
    }

    private void DisposePeerSendChannels(string peerId)
    {
        if (!_peerSendChannels.TryRemove(peerId, out var channels))
            return;

        DisposeOutboundChannel(peerId, channels.Reliable);
        DisposeOutboundChannel(peerId, channels.Unreliable);
    }

    private void DisposeOutboundChannel(string peerId, OutboundChannel channel)
    {
        try
        {
            if (!channel.WriteLock.Wait(TimeSpan.FromSeconds(1)))
                _logger.LogWarning($"[QuicTransport] Timed out waiting to dispose {channel.Name} stream for peer {peerId}");
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var stream = channel.Stream;
            channel.Stream = null;
            if (stream != null)
                _ = stream.DisposeAsync();
        }
        finally
        {
            try { channel.WriteLock.Release(); } catch (ObjectDisposedException) { /* ignore */ }
        }
    }

    private async Task ReceiveFromStreamAsync(string peerId, QuicStream stream)
    {
        try
        {
            // Read message type (1 byte)
            var messageTypeBytes = new byte[1];
            var bytesRead = await stream.ReadAsync(messageTypeBytes, _cts.Token);
            if (bytesRead == 0)
                return;

            var messageType = messageTypeBytes[0];

            switch (messageType)
            {
                case MessageTypeNormal:
                    // Persistent framed app-data: [len:4 LE][payload:N] repeating until FIN.
                    await ReceiveFramedAppDataAsync(peerId, stream);
                    break;

                case MessageTypeFileChunk:
                    await HandleFileChunkMessageAsync(peerId, stream);
                    break;

                case MessageTypeManifestRequest:
                    await HandleManifestRequestMessageAsync(peerId, stream);
                    break;

                case MessageTypeChunkRequest:
                    await HandleChunkRequestMessageAsync(peerId, stream);
                    break;

                case MessageTypeManifestResponse:
                    await HandleManifestResponseMessageAsync(peerId, stream);
                    break;

                default:
                    _logger.LogWarning($"[QuicTransport] Unknown message type: {messageType} from peer {peerId}");
                    break;
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            // Peer closed the connection/stream — not an application fault.
        }
        catch (Exception ex)
        {
            _logger.LogError($"[QuicTransport] Stream read error from peer {peerId}: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception ex) when (IsExpectedDisconnect(ex))
            {
                // ignore teardown races
            }
            catch
            {
                // ignore dispose failures
            }
        }
    }

    /// <summary>
    /// Reads length-prefixed app messages from a persistent type-0 stream until EOF/cancel.
    /// </summary>
    private async Task ReceiveFramedAppDataAsync(string peerId, QuicStream stream)
    {
        var lenBuf = new byte[4];
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(lenBuf, _cts.Token);
            }
            catch (Exception ex) when (IsExpectedDisconnect(ex))
            {
                break;
            }

            var length = BitConverter.ToInt32(lenBuf, 0);
            if (length < 0 || length > MaxFramedMessageBytes)
            {
                _logger.LogWarning(
                    $"[QuicTransport] Invalid framed message length {length} from peer {peerId}; closing stream");
                break;
            }

            var payload = new byte[length];
            try
            {
                await stream.ReadExactlyAsync(payload, _cts.Token);
            }
            catch (Exception ex) when (IsExpectedDisconnect(ex))
            {
                if (ex is EndOfStreamException)
                    _logger.LogWarning($"[QuicTransport] Truncated framed message from peer {peerId}");
                break;
            }

            OnDataReceived?.Invoke(peerId, payload, length);
        }
    }
    
    private async Task HandleFileChunkMessageAsync(string peerId, QuicStream stream)
    {
        // Protocol: [FileId:16][ChunkIndex:4][DataLength:4][Data:N]
        var fileIdBytes = new byte[16];
        await stream.ReadExactlyAsync(fileIdBytes, _cts.Token);
        var fileId = new Guid(fileIdBytes);
        
        var chunkIndexBytes = new byte[4];
        await stream.ReadExactlyAsync(chunkIndexBytes, _cts.Token);
        var chunkIndex = BitConverter.ToInt32(chunkIndexBytes);
        
        var dataLengthBytes = new byte[4];
        await stream.ReadExactlyAsync(dataLengthBytes, _cts.Token);
        var dataLength = BitConverter.ToInt32(dataLengthBytes);
        
        var data = new byte[dataLength];
        await stream.ReadExactlyAsync(data, _cts.Token);
        
        _logger.LogDebug($"[QuicTransport] Received file chunk: fileId={fileId}, chunkIndex={chunkIndex}, size={dataLength} bytes from peer {peerId}");
        OnFileChunkReceived?.Invoke(peerId, fileId, chunkIndex, data);
    }
    
    private async Task HandleManifestRequestMessageAsync(string peerId, QuicStream stream)
    {
        // Protocol: [FileId:16]
        var fileIdBytes = new byte[16];
        await stream.ReadExactlyAsync(fileIdBytes, _cts.Token);
        var fileId = new Guid(fileIdBytes);
        
        _logger.LogDebug($"[QuicTransport] Received manifest request: fileId={fileId} from peer {peerId}");
        // Note: Manifest response will be sent by upper layer (e.g., LargeFileTransferService)
        // For now, this is just a placeholder. The upper layer needs to listen to this event
        // and send the manifest response.
    }
    
    private async Task HandleChunkRequestMessageAsync(string peerId, QuicStream stream)
    {
        // Protocol: [FileId:16][ChunkIndex:4]
        var fileIdBytes = new byte[16];
        await stream.ReadExactlyAsync(fileIdBytes, _cts.Token);
        var fileId = new Guid(fileIdBytes);
        
        var chunkIndexBytes = new byte[4];
        await stream.ReadExactlyAsync(chunkIndexBytes, _cts.Token);
        var chunkIndex = BitConverter.ToInt32(chunkIndexBytes);
        
        _logger.LogDebug($"[QuicTransport] Received chunk request: fileId={fileId}, chunkIndex={chunkIndex} from peer {peerId}");
        OnFileChunkRequested?.Invoke(peerId, fileId, chunkIndex);
    }
    
    private async Task HandleManifestResponseMessageAsync(string peerId, QuicStream stream)
    {
        // Protocol: [FileId:16][TotalChunks:4][TotalSize:8][ChunkHashCount:4][ChunkHashes:N*64]
        var fileIdBytes = new byte[16];
        await stream.ReadExactlyAsync(fileIdBytes, _cts.Token);
        var fileId = new Guid(fileIdBytes);
        
        var totalChunksBytes = new byte[4];
        await stream.ReadExactlyAsync(totalChunksBytes, _cts.Token);
        var totalChunks = BitConverter.ToInt32(totalChunksBytes);
        
        var totalSizeBytes = new byte[8];
        await stream.ReadExactlyAsync(totalSizeBytes, _cts.Token);
        var totalSize = BitConverter.ToInt64(totalSizeBytes);
        
        var chunkHashCountBytes = new byte[4];
        await stream.ReadExactlyAsync(chunkHashCountBytes, _cts.Token);
        var chunkHashCount = BitConverter.ToInt32(chunkHashCountBytes);
        
        var chunkHashes = new string[chunkHashCount];
        for (int i = 0; i < chunkHashCount; i++)
        {
            var hashBytes = new byte[64]; // SHA256 hex string = 64 chars
            await stream.ReadExactlyAsync(hashBytes, _cts.Token);
            chunkHashes[i] = System.Text.Encoding.UTF8.GetString(hashBytes);
        }
        
        _logger.LogDebug($"[QuicTransport] Received manifest response: fileId={fileId}, totalChunks={totalChunks}, totalSize={totalSize} from peer {peerId}");
        OnFileManifestReceived?.Invoke(peerId, fileId, totalChunks, totalSize, chunkHashes);
    }

    public void SendReliable(string peerId, byte[] data)
    {
        if (!_isRunning || data == null)
            return;

        _ = SendFramedAsync(peerId, data, unreliable: false);
    }

    public void SendUnreliable(string peerId, byte[] data)
    {
        if (!_isRunning || data == null)
            return;

        // Separate persistent stream (not datagrams — System.Net.Quic has no datagram API yet).
        // Keeps telemetry off the media write lock / stream HOL queue.
        _ = SendFramedAsync(peerId, data, unreliable: true);
    }

    private async Task SendFramedAsync(string peerId, byte[] data, bool unreliable)
    {
        if (!_connections.TryGetValue(peerId, out var connection))
            return;

        if (data.Length > MaxFramedMessageBytes)
        {
            _logger.LogWarning(
                $"[QuicTransport] Dropping oversized {(unreliable ? "unreliable" : "reliable")} payload ({data.Length} B) to peer {peerId}");
            return;
        }

        var channels = _peerSendChannels.GetOrAdd(peerId, static _ => new PeerSendChannels());
        var channel = unreliable ? channels.Unreliable : channels.Reliable;

        try
        {
            await channel.WriteLock.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (channel.Stream == null)
            {
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, _cts.Token);
                // Channel header once: type 0, then repeating [len:4][payload]
                await stream.WriteAsync(new[] { MessageTypeNormal }, _cts.Token);
                channel.Stream = stream;
                _logger.LogInfo(
                    $"[QuicTransport] Opened persistent {channel.Name} stream to peer {peerId}");
            }

            var lenBytes = BitConverter.GetBytes(data.Length);
            await channel.Stream.WriteAsync(lenBytes, _cts.Token);
            await channel.Stream.WriteAsync(data, _cts.Token);
            await channel.Stream.FlushAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            await ResetOutboundStreamAsync(channel);
            HandlePeerDisconnect(peerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                $"[QuicTransport] Send {channel.Name} error to peer {peerId}: {ex.Message}", ex);
            await ResetOutboundStreamAsync(channel);
            // Do not tear down the whole peer on a single stream write failure — next send reopens.
        }
        finally
        {
            try { channel.WriteLock.Release(); } catch (ObjectDisposedException) { /* ignore */ }
        }
    }

    private static async Task ResetOutboundStreamAsync(OutboundChannel channel)
    {
        var stream = channel.Stream;
        channel.Stream = null;
        if (stream == null)
            return;

        try
        {
            await stream.DisposeAsync();
        }
        catch
        {
            // ignore dispose races
        }
    }

    public void PollEvents()
    {
        // QUIC is fully async, no polling needed
        // Events are handled via callbacks
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _cts.Cancel();
        _isRunning = false;

        foreach (var connection in _connections.Values)
        {
            connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }

        foreach (var peerId in _peerSendChannels.Keys.ToArray())
            DisposePeerSendChannels(peerId);

        _connections.Clear();
        _peerSendChannels.Clear();

        _clientConnection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        _listener?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));

        _logger.LogInfo("[QuicTransport] Stopped");
    }

    public IEnumerable<string> GetConnectedPeers()
    {
        return _connections.Keys.ToArray();
    }
    
    // ========== Large File Transfer Implementation ==========
    
    public void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data)
    {
        if (!_connections.TryGetValue(peerId, out var connection))
        {
            _logger.LogWarning($"[QuicTransport] SendFileChunk: Peer {peerId} not connected");
            return;
        }
        
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _cts.Token);
                
                // Protocol: [MessageType:1][FileId:16][ChunkIndex:4][DataLength:4][Data:N]
                var fileIdBytes = fileId.ToByteArray();
                var chunkIndexBytes = BitConverter.GetBytes(chunkIndex);
                var dataLengthBytes = BitConverter.GetBytes(data.Length);
                
                await stream.WriteAsync(new[] { MessageTypeFileChunk }, _cts.Token);
                await stream.WriteAsync(fileIdBytes, _cts.Token);
                await stream.WriteAsync(chunkIndexBytes, _cts.Token);
                await stream.WriteAsync(dataLengthBytes, _cts.Token);
                await stream.WriteAsync(data, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                
                stream.CompleteWrites();
                await stream.DisposeAsync();
                
                _logger.LogDebug($"[QuicTransport] Sent file chunk: fileId={fileId}, chunkIndex={chunkIndex}, size={data.Length} bytes to peer {peerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QuicTransport] SendFileChunk error to peer {peerId}: {ex.Message}", ex);
                HandlePeerDisconnect(peerId);
            }
        });
    }
    
    public void RequestFileManifest(string peerId, Guid fileId)
    {
        if (!_connections.TryGetValue(peerId, out var connection))
        {
            _logger.LogWarning($"[QuicTransport] RequestFileManifest: Peer {peerId} not connected");
            return;
        }
        
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _cts.Token);
                
                // Protocol: [MessageType:1][FileId:16]
                var fileIdBytes = fileId.ToByteArray();
                
                await stream.WriteAsync(new[] { MessageTypeManifestRequest }, _cts.Token);
                await stream.WriteAsync(fileIdBytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                
                stream.CompleteWrites();
                await stream.DisposeAsync();
                
                _logger.LogDebug($"[QuicTransport] Requested file manifest: fileId={fileId} from peer {peerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QuicTransport] RequestFileManifest error to peer {peerId}: {ex.Message}", ex);
                HandlePeerDisconnect(peerId);
            }
        });
    }
    
    public void RequestFileChunk(string peerId, Guid fileId, int chunkIndex)
    {
        if (!_connections.TryGetValue(peerId, out var connection))
        {
            _logger.LogWarning($"[QuicTransport] RequestFileChunk: Peer {peerId} not connected");
            return;
        }
        
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _cts.Token);
                
                // Protocol: [MessageType:1][FileId:16][ChunkIndex:4]
                var fileIdBytes = fileId.ToByteArray();
                var chunkIndexBytes = BitConverter.GetBytes(chunkIndex);
                
                await stream.WriteAsync(new[] { MessageTypeChunkRequest }, _cts.Token);
                await stream.WriteAsync(fileIdBytes, _cts.Token);
                await stream.WriteAsync(chunkIndexBytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                
                stream.CompleteWrites();
                await stream.DisposeAsync();
                
                _logger.LogDebug($"[QuicTransport] Requested file chunk: fileId={fileId}, chunkIndex={chunkIndex} from peer {peerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QuicTransport] RequestFileChunk error to peer {peerId}: {ex.Message}", ex);
                HandlePeerDisconnect(peerId);
            }
        });
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
