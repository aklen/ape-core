using Ape.Core.Aether;
using Ape.Core.Logging;
using Ape.Core.Network.Services;
using Ape.Core.Network.Transport;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class AetherLabelLinkTests
{
    [Fact]
    public void Two_peers_converge_on_the_greater_actor_without_sending_the_projection_back()
    {
        using var left = Peer("peer-a");
        using var right = Peer("peer-b");
        left.Transport.AddPeer(right.Transport.LocalPeerId);
        right.Transport.AddPeer(left.Transport.LocalPeerId);

        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        Assert.Equal(AetherAdmitKind.Queued, right.Link.Submit("beta").Kind);
        left.Link.Pump();
        right.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Equal("beta", right.Label);
        var alpha = Assert.Single(left.Transport.Sent);
        var beta = Assert.Single(right.Transport.Sent);
        var decoded = AetherWire.TryDecode(alpha, "entity-1", "label", out var operation, out var fault);
        Assert.True(decoded);
        Assert.Null(fault);
        Assert.Equal("peer-a", operation!.ActorId);
        Assert.Equal("peer-a", operation.WriterId);
        Assert.Equal(3, operation.Lamport);
        Assert.Equal(2, operation.Sequence);
        Assert.Equal("alpha", operation.Changes["label"].Text);
        Assert.Equal(AetherWire.PacketType, alpha[0]);
        Assert.Equal(0, alpha[1]);
        Assert.Equal(AetherWire.WireVersion, alpha[2]);

        right.Transport.Deliver(left.Transport.LocalPeerId, alpha);
        left.Transport.Deliver(right.Transport.LocalPeerId, beta);
        left.Transport.Deliver(right.Transport.LocalPeerId, beta);
        left.Link.Pump();
        right.Link.Pump();

        Assert.Equal("beta", left.Label);
        Assert.Equal("beta", right.Label);
        Assert.Equal("beta", left.LinkHostLabel);
        Assert.Equal("beta", right.LinkHostLabel);
        Assert.Single(left.Transport.Sent);
        Assert.Single(right.Transport.Sent);
        Assert.Equal(AetherAdmitKind.Queued, left.Link.LastRemoteAdmit!.Value.Kind);
        Assert.Null(left.Link.LastRemoteFault);
    }

    [Fact]
    public void A_full_queue_stays_visible_and_the_refused_label_is_not_sent()
    {
        using var left = Peer("peer-a", maxCount: 1);
        using var right = Peer("peer-b");
        left.Transport.AddPeer(right.Transport.LocalPeerId);
        right.Transport.AddPeer(left.Transport.LocalPeerId);

        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        var refused = left.Link.Submit("later");
        Assert.Equal(AetherAdmitKind.Full, refused.Kind);
        Assert.Equal(0, refused.RequestId);
        left.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Single(left.Transport.Sent);

        right.Link.Submit("beta");
        right.Link.Pump();
        left.Transport.Deliver(right.Transport.LocalPeerId, Assert.Single(right.Transport.Sent));
        Assert.Equal(AetherAdmitKind.Full, left.Link.LastRemoteAdmit!.Value.Kind);
        left.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Single(left.Transport.Sent);
    }

    [Fact]
    public void A_bad_wire_version_is_not_queued()
    {
        using var left = Peer("peer-a");
        var packet = AetherWire.Encode(new AetherOperation(
            "peer-b/2",
            "entity-1",
            "peer-b",
            "peer-b",
            2,
            3,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("beta") }));
        packet[1] = 0;
        packet[2] = 2;
        var hash = System.Security.Cryptography.SHA256.HashData(packet.AsSpan(0, packet.Length - 32));
        hash.CopyTo(packet, packet.Length - 32);
        left.Transport.Deliver("peer-b", packet);
        Assert.Equal("Wire version is not supported.", left.Link.LastRemoteFault);
        Assert.Null(left.Link.LastRemoteAdmit);
        left.Link.Pump();
        Assert.Null(left.Label);
        Assert.Empty(left.Transport.Sent);
    }

    [Fact]
    public void A_failed_send_is_tried_again_on_the_next_pump()
    {
        using var left = Peer("peer-a");
        left.Transport.AddPeer("peer-b");
        left.Transport.FailNextSends = 1;
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);

        var failed = Assert.Throws<IOException>(() => left.Link.Pump());
        Assert.Equal("Send failed.", failed.Message);
        Assert.Empty(left.Transport.Sent);
        Assert.Equal("alpha", left.Label);

        left.Link.Pump();
        var sent = Assert.Single(left.Transport.Sent);
        Assert.True(AetherWire.TryDecode(sent, "entity-1", "label", out var operation, out _));
        Assert.Equal("alpha", operation!.Changes["label"].Text);
        Assert.Equal("alpha", left.Label);
    }

    [Fact]
    public void A_stopped_link_does_not_accept_another_packet()
    {
        using var left = Peer("peer-a");
        var packet = AetherWire.Encode(new AetherOperation(
            "peer-b/2",
            "entity-1",
            "peer-b",
            "peer-b",
            2,
            3,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("beta") }));

        left.Link.Dispose();
        left.Transport.Deliver("peer-b", packet);

        Assert.Null(left.Link.LastRemoteAdmit);
        Assert.Null(left.Link.LastRemoteFault);
        Assert.Equal(0, left.Host.PendingCount);
        Assert.Null(left.LinkHostLabel);
    }

    [Fact]
    public void Reconnect_exchanges_the_current_label_and_an_older_one_does_not_win()
    {
        using var left = Peer("peer-a");
        using var right = Peer("peer-b");

        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        left.Link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, right.Link.Submit("mid").Kind);
        right.Link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, right.Link.Submit("beta").Kind);
        right.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Equal("beta", right.Label);
        Assert.Empty(left.Transport.Sent);
        Assert.Empty(right.Transport.Sent);

        left.Transport.AddPeer(right.Transport.LocalPeerId);
        right.Transport.AddPeer(left.Transport.LocalPeerId);
        var alpha = Assert.Single(left.Transport.Sent);
        var beta = Assert.Single(right.Transport.Sent);
        Assert.Equal("alpha", LabelOf(alpha));
        Assert.Equal("beta", LabelOf(beta));

        right.Transport.Deliver(left.Transport.LocalPeerId, alpha);
        left.Transport.Deliver(right.Transport.LocalPeerId, beta);
        left.Link.Pump();
        right.Link.Pump();

        Assert.Equal("beta", left.Label);
        Assert.Equal("beta", right.Label);
        Assert.Equal("beta", left.LinkHostLabel);
        Assert.Equal("beta", right.LinkHostLabel);
        Assert.Single(left.Transport.Sent);
        Assert.Single(right.Transport.Sent);

        left.Transport.Sent.Clear();
        right.Transport.Sent.Clear();
        left.Link.CatchUp();
        right.Link.CatchUp();
        Assert.Equal("alpha", LabelOf(Assert.Single(left.Transport.Sent)));
        Assert.Equal("beta", LabelOf(Assert.Single(right.Transport.Sent)));
    }

    [Fact]
    public void A_rejected_remote_write_is_not_forwarded_on_catch_up()
    {
        using var left = Peer("peer-a");
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        left.Link.Pump();

        var stolen = AetherWire.Encode(new AetherOperation(
            "peer-a/2",
            "entity-1",
            "peer-a",
            "peer-a",
            2,
            99,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("stolen") }));
        left.Transport.Deliver("peer-b", stolen);
        Assert.Equal(AetherAdmitKind.Queued, left.Link.LastRemoteAdmit!.Value.Kind);
        left.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Equal("alpha", left.LinkHostLabel);

        left.Transport.AddPeer("peer-b");
        Assert.Equal("alpha", LabelOf(Assert.Single(left.Transport.Sent)));
    }

    [Fact]
    public void Catch_up_blocked_by_a_full_outgoing_queue_resumes_on_the_next_pump()
    {
        using var left = Peer("peer-a", maxOutgoing: 1);
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        left.Link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("beta").Kind);
        left.Store.RestoreActorSequence("peer-a", long.MaxValue);

        left.Transport.AddPeer("peer-b");
        Assert.Empty(left.Transport.Sent);

        left.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Equal("alpha", LabelOf(Assert.Single(left.Transport.Sent)));
    }

    [Fact]
    public void A_full_receiver_keeps_catch_up_until_the_queue_has_room()
    {
        using var left = Peer("peer-a");
        using var right = Peer("peer-b", maxCount: 1);
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("old").Kind);
        left.Link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("newer").Kind);
        left.Link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, right.Link.Submit("held").Kind);
        right.Link.Pump();
        Assert.Equal("newer", left.Label);
        Assert.Equal("held", right.Label);

        left.Transport.AddPeer(right.Transport.LocalPeerId);
        right.Transport.AddPeer(left.Transport.LocalPeerId);
        var newer = Assert.Single(left.Transport.Sent);
        Assert.Equal("newer", LabelOf(newer));
        right.Transport.Deliver(left.Transport.LocalPeerId, newer);
        Assert.Equal(AetherAdmitKind.Full, right.Link.LastRemoteAdmit!.Value.Kind);
        Assert.Equal("held", right.Label);
        Assert.Equal("held", right.LinkHostLabel);

        using var dir = new TempDir();
        right.Host.Save(new AetherArchive(dir.Path));
        Assert.Equal(0, right.Host.PendingCount);
        right.Link.Pump();

        Assert.Equal("newer", right.Label);
        Assert.Equal("newer", right.LinkHostLabel);
        left.Transport.Deliver(right.Transport.LocalPeerId, Assert.Single(right.Transport.Sent));
        left.Link.Pump();
        Assert.Equal("newer", left.Label);
    }

    [Fact]
    public void A_yielded_label_is_not_written_by_replica_deserialize_and_does_not_look_changed()
    {
        var kept = new TestStubEntity();
        var incoming = new TestStubEntity { Counter = 7, Label = "stolen" };
        kept.YieldPropertyToAether(nameof(TestStubEntity.Label));
        using (SceneMutationScope.Begin())
        {
            kept.Label = "kept";
            kept.SnapshotProperties();
            kept.Deserialize(incoming.Serialize());
            kept.SnapshotProperties();
        }

        Assert.Equal("kept", kept.Label);
        Assert.Equal(7, kept.Counter);
        Assert.False(kept.HasChanges());

        using (SceneMutationScope.Begin())
            kept.Counter = 8;
        Assert.True(kept.HasChanges());
    }

    private static string? LabelOf(byte[] packet)
    {
        Assert.True(AetherWire.TryDecode(packet, "entity-1", "label", out var operation, out _));
        return operation!.Changes["label"].Text;
    }

    private static PeerPeer Peer(string actor, int maxCount = 8, int maxOutgoing = 64)
    {
        var transport = new ScriptedTransport(actor);
        var network = new NetworkManager(transport);
        var sp = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(
            scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1"),
            log);
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        var published = new AetherOperation(
            "pub-" + actor,
            "entity-1",
            actor,
            actor,
            1,
            1,
            new Dictionary<string, FieldValue>(),
            AetherEffect.Publish,
            "catalog");
        store.Apply(published, observeClock: true);
        store.RestoreActorSequence(actor, 1);
        var host = new AetherHost(store, 4096, maxCount);
        var link = new AetherLabelLink(
            network,
            host,
            scene,
            log,
            "entity-1",
            actor,
            actor,
            "catalog",
            maxOutgoing: maxOutgoing);
        return new PeerPeer(transport, link, scene, store, host);
    }

    private sealed class PeerPeer : IDisposable
    {
        public PeerPeer(
            ScriptedTransport transport,
            AetherLabelLink link,
            ISceneManager scene,
            AetherReducer store,
            AetherHost host)
        {
            Transport = transport;
            Link = link;
            Scene = scene;
            Store = store;
            Host = host;
        }

        public ScriptedTransport Transport { get; }

        public AetherLabelLink Link { get; }

        public ISceneManager Scene { get; }

        public AetherReducer Store { get; }

        public AetherHost Host { get; }

        public string? Label => ((TestStubEntity)Scene.GetEntity("entity-1")!).Label;

        public string? LinkHostLabel => Store.ResolveLww("entity-1", "label")?.Text;

        public void Dispose()
        {
            Link.Dispose();
            Transport.Dispose();
        }
    }

#pragma warning disable CS0067
    private sealed class ScriptedTransport : INetworkTransport
    {
        private readonly List<string> _peers = new();

        public ScriptedTransport(string localPeerId) => LocalPeerId = localPeerId;

        public string TransportType => "scripted";

        public string LocalPeerId { get; }

        public List<byte[]> Sent { get; } = new();

        public int FailNextSends { get; set; }

        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[], int>? OnDataReceived;
        public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
        public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
        public event Action<string, Guid, int>? OnFileChunkRequested;

        public void AddPeer(string peerId)
        {
            _peers.Add(peerId);
            OnPeerConnected?.Invoke(peerId);
        }

        public void Deliver(string fromPeerId, byte[] data) =>
            OnDataReceived?.Invoke(fromPeerId, data, data.Length);

        public void SendReliable(string peerId, byte[] data) => Take(data);

        public void SendUnreliable(string peerId, byte[] data) => Take(data);

        private void Take(byte[] data)
        {
            if (FailNextSends > 0)
            {
                FailNextSends--;
                throw new IOException("Send failed.");
            }

            Sent.Add(data.ToArray());
        }

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

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
#pragma warning restore CS0067
}
