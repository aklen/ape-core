using System.Security.Cryptography;
using Ape.Core.Aether;
using Ape.Core.Network;
using Ape.Core.Network.FileTransfer;
using Ape.Core.Network.Services;
using Ape.Core.Network.Transport;
using Ape.Core.Replication;

namespace Ape.Core.Tests;

public sealed class AetherCheckpointLinkTests
{
    [Fact]
    public async Task Authenticated_peers_transfer_one_bounded_packet_per_pump_and_resume_on_a_new_connection()
    {
        using var profile = new TempDir();
        using var chunks = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var artifact = Checkpoint(owner, new string('q', 3500));
        var publisherNetwork = new FakeNetwork(owner.PrincipalId);
        var receiverNetwork = new FakeNetwork(new string('b', 64));
        publisherNetwork.AddPeer("client", receiverNetwork.PrincipalId, authenticated: true);
        receiverNetwork.AddPeer("server-1", owner.PrincipalId, authenticated: true);
        using var publisher = new AetherCheckpointLink(publisherNetwork, new MemoryChunkStore());
        using var receiver = new AetherCheckpointLink(receiverNetwork, new FileChunkStore(chunks.Path));
        publisher.Publish(artifact, [receiverNetwork.PrincipalId], AetherCheckpointManifest.MinimumChunkSize);
        receiver.BeginDownload("server-1", artifact.Sha256);

        await PumpAndRelay(receiver, receiverNetwork, publisher, publisherNetwork, "client", "server-1");
        await PumpAndRelay(receiver, receiverNetwork, publisher, publisherNetwork, "client", "server-1");
        Assert.True(receiver.IsDownloading);
        receiverNetwork.Disconnect("server-1");
        receiverNetwork.AddPeer("server-2", owner.PrincipalId, authenticated: true);

        AetherReducer? restored = null;
        for (var turn = 0; turn < 100 && restored is null; turn++)
        {
            await PumpAndRelay(receiver, receiverNetwork, publisher, publisherNetwork, "client", "server-2");
            restored = receiver.TakeCompleted();
        }

        Assert.NotNull(restored);
        Assert.Equal(new string('q', 3500), restored.ResolveLww("entity-1", "label")?.Text);
        Assert.All(receiverNetwork.SendsPerPump, count => Assert.InRange(count, 0, 1));
        Assert.All(publisherNetwork.SendsPerPump, count => Assert.InRange(count, 0, 1));
        Assert.Equal(0, receiverNetwork.ReliableSends);
        Assert.Equal(0, publisherNetwork.ReliableSends);
    }

    [Fact]
    public async Task Unauthenticated_checkpoint_packet_never_enters_the_link_queue()
    {
        var network = new FakeNetwork("receiver");
        network.AddPeer("peer", new string('c', 64), authenticated: false);
        using var link = new AetherCheckpointLink(network, new MemoryChunkStore());
        var packet = AetherCheckpointWire.Encode(new AetherCheckpointPacket(
            AetherCheckpointPacketKind.ManifestRequest, new string('0', 64)));

        network.Receive("peer", packet);
        var result = await link.PumpAsync();

        Assert.Equal(0, link.PendingIncoming);
        Assert.Equal(0, result.IncomingMessages);
        Assert.Equal("Checkpoint peer is not authenticated.", link.LastFault);
    }

    [Fact]
    public async Task Authenticated_peer_outside_the_reader_list_gets_no_manifest()
    {
        using var profile = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var artifact = Checkpoint(owner, "secret");
        var network = new FakeNetwork(owner.PrincipalId);
        network.AddPeer("intruder", new string('d', 64), authenticated: true);
        using var link = new AetherCheckpointLink(network, new MemoryChunkStore());
        link.Publish(artifact, [new string('e', 64)]);
        var request = AetherCheckpointWire.Encode(new AetherCheckpointPacket(
            AetherCheckpointPacketKind.ManifestRequest, artifact.Sha256));

        network.Receive("intruder", request);
        await link.PumpAsync();

        Assert.Empty(network.TakeSent());
        Assert.Equal(0, link.PendingOutgoing);
    }

    [Fact]
    public void Network_manager_routes_bulk_to_the_optional_transport_plane()
    {
        var transport = new BulkTransport();
        transport.ConnectPeer("7");
        using var network = new NetworkManager(transport, "local", allowUnauthenticated: true);

        Assert.True(((IBulkNetworkSender)network).TrySendBulk("7", [1, 2, 3]));

        Assert.Equal(1, transport.BulkSends);
        Assert.Equal(0, transport.ReliableSends);
    }

    [Fact]
    public async Task Full_bulk_queue_keeps_the_request_for_the_next_pump()
    {
        var network = new FakeNetwork(new string('b', 64));
        network.AddPeer("server", new string('a', 64), authenticated: true);
        network.AcceptBulk = false;
        using var link = new AetherCheckpointLink(network, new MemoryChunkStore());
        link.BeginDownload("server", new string('1', 64));

        var refused = await link.PumpAsync();
        network.AcceptBulk = true;
        var accepted = await link.PumpAsync();

        Assert.Equal(0, refused.OutgoingMessages);
        Assert.Equal(1, link.PendingOutgoing + accepted.OutgoingMessages);
        Assert.Equal(1, accepted.OutgoingMessages);
    }

    [Fact]
    public void Checkpoint_wire_rejects_corruption_before_payload_decode()
    {
        var packet = AetherCheckpointWire.Encode(new AetherCheckpointPacket(
            AetherCheckpointPacketKind.ChunkRequest, new string('2', 64), 3));
        packet[^1] ^= 1;

        var error = Assert.Throws<AetherProtocolException>(() => AetherCheckpointWire.Decode(packet));

        Assert.Equal("Checkpoint packet checksum failed.", error.Message);
    }

    [Fact]
    public async Task Missing_manifest_response_is_requested_again_after_the_deadline()
    {
        var time = new ManualTime();
        var network = new FakeNetwork(new string('b', 64));
        network.AddPeer("server", new string('a', 64), authenticated: true);
        using var link = new AetherCheckpointLink(network, new MemoryChunkStore(), time: time,
            retryAfter: TimeSpan.FromSeconds(1));
        link.BeginDownload("server", new string('1', 64));

        await link.PumpAsync();
        Assert.Single(network.TakeSent());
        await link.PumpAsync();
        Assert.Empty(network.TakeSent());
        time.Now += TimeSpan.FromSeconds(1);
        await link.PumpAsync();

        Assert.Single(network.TakeSent());
    }

    [Fact]
    public async Task Concurrent_quic_callbacks_stay_inside_the_inbox_bound()
    {
        var network = new FakeNetwork(new string('b', 64));
        network.AddPeer("peer", new string('a', 64), authenticated: true);
        using var link = new AetherCheckpointLink(network, new MemoryChunkStore(),
            maxIncomingMessages: 8, maxIncomingBytes: 4096);
        var packet = AetherCheckpointWire.Encode(new AetherCheckpointPacket(
            AetherCheckpointPacketKind.ManifestRequest, new string('1', 64)));

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => network.Receive("peer", packet))));

        Assert.InRange(link.PendingIncoming, 1, 8);
        Assert.Equal("Checkpoint incoming queue is full.", link.LastFault);
        await link.PumpAsync(incomingMessageBudget: 8);
        Assert.Equal(0, link.PendingIncoming);
    }

    private static async Task PumpAndRelay(
        AetherCheckpointLink receiver,
        FakeNetwork receiverNetwork,
        AetherCheckpointLink publisher,
        FakeNetwork publisherNetwork,
        string publisherConnection,
        string receiverConnection)
    {
        receiverNetwork.BeginPump();
        await receiver.PumpAsync(outgoingMessageBudget: 1, outgoingByteBudget: 300_000);
        foreach (var packet in receiverNetwork.TakeSent())
            publisherNetwork.Receive(publisherConnection, packet.Data);

        publisherNetwork.BeginPump();
        await publisher.PumpAsync(outgoingMessageBudget: 1, outgoingByteBudget: 300_000);
        foreach (var packet in publisherNetwork.TakeSent())
            receiverNetwork.Receive(receiverConnection, packet.Data);
    }

    private static AetherCheckpointArtifact Checkpoint(NetworkManager owner, string label)
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        var actor = AetherActorId.Format(owner.PrincipalId, "session");
        var create = new AetherOperation("create", "entity-1", owner.PrincipalId, actor, 1, 1,
            new Dictionary<string, FieldValue>(), AetherEffect.CreateEntity,
            entityTypeId: TestStubEntity.RegistryTypeId, creatorId: owner.PrincipalId, ownerId: owner.PrincipalId);
        var signer = (IPrincipalSigner)owner;
        Assert.True(signer.TrySignPrincipal(create.AuthorityPayload(), out var key, out var signature));
        reducer.Apply(AetherAuthority.Seal(create, key, signature), observeClock: true);
        reducer.Apply(new AetherOperation("write", "entity-1", owner.PrincipalId, actor, 2, 2,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label(label) }), observeClock: true);
        return AetherCheckpoint.Create(reducer, "entity-1", owner);
    }

    private sealed class FakeNetwork : INetworkManager, IBulkNetworkSender
    {
        private readonly Dictionary<string, PeerConnectionContext> _peers = new(StringComparer.Ordinal);
        private readonly List<(string ConnectionId, byte[] Data)> _sent = [];
        private int _pumpStart;
        public FakeNetwork(string principalId) => PrincipalId = principalId;
        public string LocalPeerId => PrincipalId + "-process";
        public string PrincipalId { get; }
        public string Role => "peer";
        public int ReliableSends { get; private set; }
        public bool AcceptBulk { get; set; } = true;
        public List<int> SendsPerPump { get; } = [];
        public event Action<string, byte[]>? OnDataReceived;
        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<PeerConnectionContext>? OnPeerIdentified;
        public void AddPeer(string connectionId, string principalId, bool authenticated)
        {
            var peer = new PeerConnectionContext(connectionId, principalId, authenticated);
            _peers[connectionId] = peer;
            OnPeerConnected?.Invoke(connectionId);
            OnPeerIdentified?.Invoke(peer);
        }
        public void Disconnect(string connectionId)
        {
            _peers.Remove(connectionId);
            OnPeerDisconnected?.Invoke(connectionId);
        }
        public void Receive(string connectionId, byte[] data) => OnDataReceived?.Invoke(connectionId, data);
        public void BeginPump()
        {
            if (SendsPerPump.Count > 0 || _pumpStart > 0)
                SendsPerPump.Add(_sent.Count - _pumpStart);
            _pumpStart = _sent.Count;
        }
        public IReadOnlyList<(string ConnectionId, byte[] Data)> TakeSent()
        {
            var result = _sent.Skip(_pumpStart).Select(item => (item.ConnectionId, item.Data.ToArray())).ToArray();
            SendsPerPump.Add(result.Length);
            _sent.Clear();
            _pumpStart = 0;
            return result;
        }
        bool IBulkNetworkSender.TrySendBulk(string connectionId, byte[] data)
        {
            if (!_peers.ContainsKey(connectionId) || !AcceptBulk) return false;
            _sent.Add((connectionId, data.ToArray()));
            return true;
        }
        public void Broadcast(byte[] data, bool reliable = true) => throw new NotSupportedException();
        public void Send(string peerId, byte[] data, bool reliable = true) { ReliableSends++; }
        public bool TryGetPeer(string connectionId, out PeerConnectionContext peer) => _peers.TryGetValue(connectionId, out peer);
        public IReadOnlyList<string> GetConnectedPeers() => _peers.Keys.ToArray();
        public void StartServer(int port, string? peerName = null, string? role = null) { }
        public void Connect(string address, int port, string? peerName = null, string? role = null) { }
        public void Stop() { }
        public void PollEvents() { }
        public IEnumerable<string> GetLocalIPAddresses() => [];
        public string GetPreferredLocalIPAddress() => "127.0.0.1";
    }

#pragma warning disable CS0067
    private sealed class BulkTransport : INetworkTransport, IBulkNetworkTransport
    {
        private readonly List<string> _peers = [];
        public string TransportType => "bulk-test";
        public string LocalPeerId => "local";
        public int BulkSends { get; private set; }
        public int ReliableSends { get; private set; }
        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[], int>? OnDataReceived;
        public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
        public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
        public event Action<string, Guid, int>? OnFileChunkRequested;
        public void ConnectPeer(string id) { _peers.Add(id); OnPeerConnected?.Invoke(id); }
        public bool TrySendBulk(string peerId, byte[] data) { BulkSends++; return true; }
        public void SendReliable(string peerId, byte[] data) => ReliableSends++;
        public void SendUnreliable(string peerId, byte[] data) { }
        public IEnumerable<string> GetConnectedPeers() => _peers;
        public void StartServer(int port, string? peerName = null, string? role = null) { }
        public void Connect(string host, int port, string? peerName = null, string? role = null) { }
        public void PollEvents() { }
        public void Stop() { }
        public void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data) { }
        public void RequestFileManifest(string peerId, Guid fileId) { }
        public void RequestFileChunk(string peerId, Guid fileId, int chunkIndex) { }
        public void Dispose() { }
    }
#pragma warning restore CS0067

    private sealed class MemoryChunkStore : IChunkStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.OrdinalIgnoreCase);
        public Task<string> StoreChunkAsync(byte[] data, CancellationToken ct = default)
        {
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            _values[hash] = data.ToArray();
            return Task.FromResult(hash);
        }
        public Task<byte[]?> GetChunkAsync(string hash, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(hash, out var value) ? value.ToArray() : null);
        public bool HasChunk(string hash) => _values.ContainsKey(hash);
        public Task DeleteChunkAsync(string hash, CancellationToken ct = default)
        { _values.Remove(hash); return Task.CompletedTask; }
        public long GetStorageSize() => _values.Values.Sum(value => (long)value.Length);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-link-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
