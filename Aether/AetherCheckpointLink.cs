using Ape.Core.Network;
using Ape.Core.Network.FileTransfer;

namespace Ape.Core.Aether;

public readonly record struct AetherCheckpointPumpResult(
    int IncomingMessages,
    int IncomingBytes,
    int OutgoingMessages,
    int OutgoingBytes);

/// <summary>
/// Authenticated request/response adapter for checkpoint manifests and chunks.
/// Network callbacks only enqueue bounded packets. Storage and assembly run from <see cref="PumpAsync"/>.
/// </summary>
public sealed class AetherCheckpointLink : IDisposable
{
    public const int MaximumReadersPerArtifact = 64;
    private readonly INetworkManager _network;
    private readonly IChunkStore _chunkStore;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryAfter;
    private readonly int _maxIncomingMessages;
    private readonly int _maxIncomingBytes;
    private readonly int _maxOutgoingMessages;
    private readonly int _maxOutgoingBytes;
    private readonly int _maxPublishedArtifacts;
    private readonly int _maxPublishedBytes;
    private readonly object _inboxGate = new();
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private readonly Queue<Incoming> _incoming = new();
    private readonly Queue<ConnectionEvent> _connectionEvents = new();
    private readonly Queue<Outgoing> _outgoing = new();
    private readonly Dictionary<string, Published> _published = new(StringComparer.OrdinalIgnoreCase);
    private int _incomingBytes;
    private int _outgoingBytes;
    private int _publishedBytes;
    private Download? _download;
    private AetherReducer? _completed;
    private bool _disposed;

    public AetherCheckpointLink(
        INetworkManager network,
        IChunkStore chunkStore,
        int maxIncomingMessages = 64,
        int maxIncomingBytes = 4 * 1024 * 1024,
        int maxOutgoingMessages = 64,
        int maxOutgoingBytes = 4 * 1024 * 1024,
        int maxPublishedArtifacts = 8,
        int maxPublishedBytes = 32 * 1024 * 1024,
        TimeProvider? time = null,
        TimeSpan? retryAfter = null)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        RequirePositive(maxIncomingMessages, nameof(maxIncomingMessages));
        RequirePositive(maxIncomingBytes, nameof(maxIncomingBytes));
        RequirePositive(maxOutgoingMessages, nameof(maxOutgoingMessages));
        RequirePositive(maxOutgoingBytes, nameof(maxOutgoingBytes));
        RequirePositive(maxPublishedArtifacts, nameof(maxPublishedArtifacts));
        RequirePositive(maxPublishedBytes, nameof(maxPublishedBytes));
        if (retryAfter is { } retry && retry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        _maxIncomingMessages = maxIncomingMessages;
        _maxIncomingBytes = maxIncomingBytes;
        _maxOutgoingMessages = maxOutgoingMessages;
        _maxOutgoingBytes = maxOutgoingBytes;
        _maxPublishedArtifacts = maxPublishedArtifacts;
        _maxPublishedBytes = maxPublishedBytes;
        _time = time ?? TimeProvider.System;
        _retryAfter = retryAfter ?? TimeSpan.FromSeconds(1);
        network.OnDataReceived += OnData;
        network.OnPeerDisconnected += OnDisconnected;
        network.OnPeerIdentified += OnIdentified;
    }

    public string? LastFault { get; private set; }
    public bool IsDownloading => _download is not null && _completed is null;
    public int PendingIncoming { get { lock (_inboxGate) return _incoming.Count; } }
    public int PendingOutgoing => _outgoing.Count;

    public AetherCheckpointManifest Publish(
        AetherCheckpointArtifact artifact,
        IEnumerable<string> allowedPrincipalIds,
        int chunkSize = AetherCheckpointManifest.DefaultChunkSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(allowedPrincipalIds);
        var manifest = AetherCheckpointManifest.Create(artifact, chunkSize);
        var readers = allowedPrincipalIds.ToHashSet(StringComparer.Ordinal);
        if (readers.Count is < 1 or > MaximumReadersPerArtifact || readers.Any(id => !IsDigest(id)))
            throw new AetherProtocolException("Checkpoint reader list is invalid.");
        if (_published.ContainsKey(artifact.Sha256))
        {
            var existing = _published[artifact.Sha256];
            if (!existing.Readers.SetEquals(readers))
                throw new AetherProtocolException("Checkpoint reader list does not match its publication.");
            return existing.Manifest;
        }
        if (_published.Count >= _maxPublishedArtifacts
            || artifact.Length > _maxPublishedBytes
            || _publishedBytes > _maxPublishedBytes - artifact.Length)
            throw new AetherProtocolException("Published checkpoints do not fit.");
        _published[artifact.Sha256] = new Published(artifact, manifest, readers);
        _publishedBytes += artifact.Length;
        return manifest;
    }

    public bool Unpublish(string artifactSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_published.Remove(artifactSha256, out var removed))
            return false;
        _publishedBytes -= removed.Artifact.Length;
        return true;
    }

    public void BeginDownload(string connectionId, string expectedSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_network.TryGetPeer(connectionId, out var peer) || !peer.IsAuthenticated)
            throw new AetherProtocolException("Checkpoint peer is not authenticated.");
        ValidateDigest(expectedSha256);
        _completed = null;
        _download = new Download(expectedSha256.ToLowerInvariant(), peer.PrincipalId, connectionId);
        RemoveDownloadRequests();
    }

    public AetherReducer? TakeCompleted()
    {
        var result = _completed;
        _completed = null;
        return result;
    }

    public async Task<AetherCheckpointPumpResult> PumpAsync(
        int incomingMessageBudget = 8,
        int incomingByteBudget = 512 * 1024,
        int outgoingMessageBudget = 4,
        int outgoingByteBudget = 272 * 1024,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(incomingMessageBudget, nameof(incomingMessageBudget));
        RequirePositive(incomingByteBudget, nameof(incomingByteBudget));
        RequirePositive(outgoingMessageBudget, nameof(outgoingMessageBudget));
        RequirePositive(outgoingByteBudget, nameof(outgoingByteBudget));

        await _pumpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await PumpCoreAsync(incomingMessageBudget, incomingByteBudget,
                outgoingMessageBudget, outgoingByteBudget, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    private async Task<AetherCheckpointPumpResult> PumpCoreAsync(
        int incomingMessageBudget,
        int incomingByteBudget,
        int outgoingMessageBudget,
        int outgoingByteBudget,
        CancellationToken cancellationToken)
    {
        ApplyConnectionEvents();

        var incomingMessages = 0;
        var incomingBytes = 0;
        while (incomingMessages < incomingMessageBudget)
        {
            Incoming next;
            lock (_inboxGate)
            {
                if (!_incoming.TryPeek(out next!)
                    || (incomingMessages != 0 && incomingBytes > incomingByteBudget - next.Data.Length))
                    break;
                _incoming.Dequeue();
                _incomingBytes -= next.Data.Length;
            }
            incomingMessages++;
            incomingBytes += next.Data.Length;
            try
            {
                await ProcessAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (AetherProtocolException ex)
            {
                LastFault = ex.Message;
            }
        }

        await ScheduleDownloadAsync(cancellationToken).ConfigureAwait(false);
        var outgoingMessages = 0;
        var outgoingBytes = 0;
        while (_outgoing.TryPeek(out var next)
            && outgoingMessages < outgoingMessageBudget
            && (outgoingMessages == 0 || outgoingBytes <= outgoingByteBudget - next.Data.Length))
        {
            if (!IsAuthenticatedConnection(next.ConnectionId, next.PrincipalId)
                || !_network.TrySendBulk(next.ConnectionId, next.Data))
                break;
            _outgoing.Dequeue();
            _outgoingBytes -= next.Data.Length;
            outgoingMessages++;
            outgoingBytes += next.Data.Length;
            if (_download is { } download && next.DownloadChunkIndex is { } index)
                download.Outstanding[index] = _time.GetUtcNow();
            if (_download is { } manifestDownload && next.IsManifestRequest)
                manifestDownload.ManifestRequestedAt = _time.GetUtcNow();
        }
        return new AetherCheckpointPumpResult(incomingMessages, incomingBytes, outgoingMessages, outgoingBytes);
    }

    public void Dispose()
    {
        _pumpGate.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _network.OnDataReceived -= OnData;
            _network.OnPeerDisconnected -= OnDisconnected;
            _network.OnPeerIdentified -= OnIdentified;
            lock (_inboxGate)
            {
                _incoming.Clear();
                _connectionEvents.Clear();
                _incomingBytes = 0;
            }
            _outgoing.Clear();
            _published.Clear();
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    private void OnData(string connectionId, byte[] data)
    {
        if (_disposed || !AetherCheckpointWire.IsPacket(data))
            return;
        if (!IsAuthenticatedConnection(connectionId, out var peer))
        {
            LastFault = "Checkpoint peer is not authenticated.";
            return;
        }
        lock (_inboxGate)
        {
            if (data.Length > AetherCheckpointWire.MaximumPacketBytes
                || _incoming.Count >= _maxIncomingMessages
                || data.Length > _maxIncomingBytes
                || _incomingBytes > _maxIncomingBytes - data.Length)
            {
                LastFault = "Checkpoint incoming queue is full.";
                return;
            }
            _incoming.Enqueue(new Incoming(connectionId, peer.PrincipalId, data.ToArray()));
            _incomingBytes += data.Length;
        }
    }

    private async Task ProcessAsync(Incoming incoming, CancellationToken cancellationToken)
    {
        if (!IsAuthenticatedConnection(incoming.ConnectionId, incoming.PrincipalId))
            throw new AetherProtocolException("Checkpoint peer is not authenticated.");
        var packet = AetherCheckpointWire.Decode(incoming.Data);
        switch (packet.Kind)
        {
            case AetherCheckpointPacketKind.ManifestRequest:
                if (_published.TryGetValue(packet.ArtifactSha256, out var publishedManifest)
                    && publishedManifest.Readers.Contains(incoming.PrincipalId))
                    Enqueue(incoming.ConnectionId, incoming.PrincipalId,
                        new AetherCheckpointPacket(AetherCheckpointPacketKind.Manifest,
                            packet.ArtifactSha256, Payload: publishedManifest.Manifest.Encode()));
                break;
            case AetherCheckpointPacketKind.ChunkRequest:
                if (_published.TryGetValue(packet.ArtifactSha256, out var publishedChunk)
                    && publishedChunk.Readers.Contains(incoming.PrincipalId))
                {
                    var bytes = AetherCheckpointReceiver.ReadChunk(
                        publishedChunk.Artifact, publishedChunk.Manifest, packet.ChunkIndex);
                    Enqueue(incoming.ConnectionId, incoming.PrincipalId,
                        new AetherCheckpointPacket(AetherCheckpointPacketKind.Chunk,
                            packet.ArtifactSha256, packet.ChunkIndex, bytes));
                }
                break;
            case AetherCheckpointPacketKind.Manifest:
                if (MatchesDownload(incoming, packet.ArtifactSha256, out var manifestDownload))
                {
                    if (manifestDownload.Receiver is not null)
                        break;
                    var manifest = AetherCheckpointManifest.Decode(packet.Payload!);
                    manifestDownload.Receiver = new AetherCheckpointReceiver(manifest,
                        manifestDownload.ExpectedSha256, _chunkStore);
                    manifestDownload.ManifestRequestedAt = null;
                    manifestDownload.NeedRefresh = true;
                    manifestDownload.Needed.Clear();
                    manifestDownload.NeededSet.Clear();
                    manifestDownload.Outstanding.Clear();
                }
                break;
            case AetherCheckpointPacketKind.Chunk:
                if (MatchesDownload(incoming, packet.ArtifactSha256, out var chunkDownload)
                    && chunkDownload.Receiver is not null)
                {
                    await chunkDownload.Receiver.AcceptChunkAsync(packet.ChunkIndex, packet.Payload!, cancellationToken)
                        .ConfigureAwait(false);
                    chunkDownload.Outstanding.Remove(packet.ChunkIndex);
                    chunkDownload.NeededSet.Remove(packet.ChunkIndex);
                    if (chunkDownload.Outstanding.Count == 0 && chunkDownload.Needed.Count == 0)
                        chunkDownload.NeedRefresh = true;
                }
                break;
        }
        LastFault = null;
    }

    private async Task ScheduleDownloadAsync(CancellationToken cancellationToken)
    {
        if (_download is not { ConnectionId: { } connectionId } download
            || !IsAuthenticatedConnection(connectionId, download.PrincipalId))
            return;
        var now = _time.GetUtcNow();
        if (download.Receiver is null)
        {
            if (!HasQueuedManifestRequest(download)
                && (download.ManifestRequestedAt is null || now - download.ManifestRequestedAt >= _retryAfter))
                Enqueue(connectionId, download.PrincipalId,
                    new AetherCheckpointPacket(AetherCheckpointPacketKind.ManifestRequest, download.ExpectedSha256),
                    isManifestRequest: true);
            return;
        }

        foreach (var expired in download.Outstanding
                     .Where(pair => now - pair.Value >= _retryAfter)
                     .Select(pair => pair.Key).ToArray())
        {
            download.Outstanding.Remove(expired);
            AddNeeded(download, expired);
        }
        if (download.NeedRefresh && download.Outstanding.Count == 0 && download.Needed.Count == 0)
        {
            var missing = await download.Receiver.MissingChunksAsync(cancellationToken).ConfigureAwait(false);
            download.NeedRefresh = false;
            foreach (var index in missing)
                AddNeeded(download, index);
            if (missing.Count == 0)
            {
                _completed = await download.Receiver.CompleteAsync(cancellationToken).ConfigureAwait(false);
                _download = null;
                RemoveDownloadRequests();
                return;
            }
        }
        while (download.Needed.TryPeek(out var index))
        {
            var packet = new AetherCheckpointPacket(AetherCheckpointPacketKind.ChunkRequest,
                download.ExpectedSha256, index);
            if (!Enqueue(connectionId, download.PrincipalId, packet, downloadChunkIndex: index))
                break;
            download.Needed.Dequeue();
            download.NeededSet.Remove(index);
        }
    }

    private bool Enqueue(
        string connectionId,
        string principalId,
        AetherCheckpointPacket packet,
        int? downloadChunkIndex = null,
        bool isManifestRequest = false)
    {
        var bytes = AetherCheckpointWire.Encode(packet);
        if (_outgoing.Count >= _maxOutgoingMessages
            || bytes.Length > _maxOutgoingBytes
            || _outgoingBytes > _maxOutgoingBytes - bytes.Length)
        {
            LastFault = "Checkpoint outgoing queue is full.";
            return false;
        }
        _outgoing.Enqueue(new Outgoing(connectionId, principalId, bytes, downloadChunkIndex, isManifestRequest));
        _outgoingBytes += bytes.Length;
        return true;
    }

    private bool MatchesDownload(Incoming incoming, string digest, out Download download)
    {
        download = _download!;
        return download is not null
            && string.Equals(download.ConnectionId, incoming.ConnectionId, StringComparison.Ordinal)
            && string.Equals(download.PrincipalId, incoming.PrincipalId, StringComparison.Ordinal)
            && string.Equals(download.ExpectedSha256, digest, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDisconnected(string connectionId)
    {
        EnqueueConnectionEvent(new ConnectionEvent(connectionId, null));
    }

    private void OnIdentified(PeerConnectionContext peer)
    {
        EnqueueConnectionEvent(new ConnectionEvent(peer.ConnectionId, peer));
    }

    private void EnqueueConnectionEvent(ConnectionEvent item)
    {
        lock (_inboxGate)
        {
            if (_connectionEvents.Count >= _maxIncomingMessages)
            {
                LastFault = "Checkpoint connection-event queue is full.";
                return;
            }
            _connectionEvents.Enqueue(item);
        }
    }

    private void ApplyConnectionEvents()
    {
        ConnectionEvent[] events;
        lock (_inboxGate)
        {
            events = _connectionEvents.ToArray();
            _connectionEvents.Clear();
        }
        foreach (var item in events)
        {
            if (item.Peer is { } peer)
                ApplyIdentified(peer);
            else
                ApplyDisconnected(item.ConnectionId);
        }
    }

    private void ApplyDisconnected(string connectionId)
    {
        DropConnectionPackets(connectionId);
        if (_download is not { } download
            || !string.Equals(download.ConnectionId, connectionId, StringComparison.Ordinal))
            return;
        download.ConnectionId = null;
        download.Outstanding.Clear();
        download.ManifestRequestedAt = null;
        download.NeedRefresh = download.Receiver is not null;
    }

    private void ApplyIdentified(PeerConnectionContext peer)
    {
        if (!peer.IsAuthenticated || _download is not { ConnectionId: null } download
            || !string.Equals(download.PrincipalId, peer.PrincipalId, StringComparison.Ordinal))
            return;
        download.ConnectionId = peer.ConnectionId;
        download.ManifestRequestedAt = null;
        download.NeedRefresh = download.Receiver is not null;
    }

    private void DropConnectionPackets(string connectionId)
    {
        if (_incoming.Count > 0)
        {
            var kept = _incoming.Where(item => item.ConnectionId != connectionId).ToArray();
            _incoming.Clear();
            _incomingBytes = 0;
            foreach (var item in kept) { _incoming.Enqueue(item); _incomingBytes += item.Data.Length; }
        }
        if (_outgoing.Count > 0)
        {
            var kept = _outgoing.Where(item => item.ConnectionId != connectionId).ToArray();
            _outgoing.Clear();
            _outgoingBytes = 0;
            foreach (var item in kept) { _outgoing.Enqueue(item); _outgoingBytes += item.Data.Length; }
        }
    }

    private void RemoveDownloadRequests()
    {
        if (_outgoing.Count == 0)
            return;
        var kept = _outgoing.Where(item => item.DownloadChunkIndex is null && !item.IsManifestRequest).ToArray();
        _outgoing.Clear();
        _outgoingBytes = 0;
        foreach (var item in kept) { _outgoing.Enqueue(item); _outgoingBytes += item.Data.Length; }
    }

    private bool HasQueuedManifestRequest(Download download) => _outgoing.Any(item =>
        item.IsManifestRequest && string.Equals(item.ConnectionId, download.ConnectionId, StringComparison.Ordinal));

    private static void AddNeeded(Download download, int index)
    {
        if (download.NeededSet.Add(index))
            download.Needed.Enqueue(index);
    }

    private bool IsAuthenticatedConnection(string connectionId, string principalId) =>
        _network.TryGetPeer(connectionId, out var peer)
        && peer.IsAuthenticated
        && string.Equals(peer.PrincipalId, principalId, StringComparison.Ordinal);

    private bool IsAuthenticatedConnection(string connectionId, out PeerConnectionContext peer) =>
        _network.TryGetPeer(connectionId, out peer) && peer.IsAuthenticated;

    private static void ValidateDigest(string digest)
    {
        if (!IsDigest(digest))
            throw new AetherProtocolException("Checkpoint digest is invalid.");
    }

    private static bool IsDigest(string? digest)
    {
        if (digest is null || digest.Length != 64)
            return false;
        try { Convert.FromHexString(digest); return true; }
        catch (FormatException) { return false; }
    }

    private static void RequirePositive(int value, string name)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(name);
    }

    private sealed record Published(
        AetherCheckpointArtifact Artifact,
        AetherCheckpointManifest Manifest,
        HashSet<string> Readers);
    private sealed record Incoming(string ConnectionId, string PrincipalId, byte[] Data);
    private sealed record ConnectionEvent(string ConnectionId, PeerConnectionContext? Peer);
    private sealed record Outgoing(
        string ConnectionId,
        string PrincipalId,
        byte[] Data,
        int? DownloadChunkIndex,
        bool IsManifestRequest);

    private sealed class Download(string expectedSha256, string principalId, string connectionId)
    {
        public string ExpectedSha256 { get; } = expectedSha256;
        public string PrincipalId { get; } = principalId;
        public string? ConnectionId { get; set; } = connectionId;
        public AetherCheckpointReceiver? Receiver { get; set; }
        public Queue<int> Needed { get; } = new();
        public HashSet<int> NeededSet { get; } = [];
        public Dictionary<int, DateTimeOffset> Outstanding { get; } = [];
        public DateTimeOffset? ManifestRequestedAt { get; set; }
        public bool NeedRefresh { get; set; }
    }
}
