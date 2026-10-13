using Ape.Core.Aether;
using Ape.Core.Logging;
using Ape.Core.Network;
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
        Assert.Equal(AetherActorId.Format("peer-a", "s1"), operation!.ActorId);
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
        packet[2] = 3;
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
    public void Stopping_the_link_keeps_the_applied_label()
    {
        using var left = Peer("peer-a");
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        left.Link.Pump();
        left.Link.Dispose();

        Assert.Equal("alpha", left.Label);
        Assert.Equal("alpha", left.LinkHostLabel);
        Assert.Equal(1, left.Store.RecordCount);
    }

    [Fact]
    public void A_stable_writer_is_admitted_on_a_receiver_slot()
    {
        using var left = Peer("process-b");
        var slot = left.Transport.OpenSlot();
        Assert.NotEqual("process-a", slot);
        var hello = Assert.Single(left.Transport.Hellos);
        Assert.Equal(slot, hello.ConnectionId);
        Assert.True(PeerHello.TryDecode(hello.Body, out var announced));
        Assert.Equal("process-b", announced);

        var actor = AetherActorId.Format("process-a", "s1");
        var packet = AetherWire.Encode(new AetherOperation(
            "process-a/s1/1",
            "entity-1",
            "process-a",
            actor,
            1,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("kept") }));
        left.Transport.DeliverSlot(slot, packet);
        Assert.Equal(0, left.Host.PendingCount);
        Assert.Null(left.Link.LastRemoteAdmit);

        left.Transport.Announce(slot, "process-a");
        Assert.Equal(AetherAdmitKind.Queued, left.Link.LastRemoteAdmit!.Value.Kind);
        left.Link.Pump();
        Assert.Equal("kept", left.LinkHostLabel);
        Assert.Equal(1, left.Store.ActorCursors.Single(cursor => cursor.ActorId == actor).Sequence);
    }

    [Fact]
    public void A_connection_does_not_replace_its_principal()
    {
        var transport = new ScriptedTransport("process-b");
        var network = new NetworkManager(transport, allowUnauthenticated: true);
        var slot = transport.OpenSlot();
        transport.Announce(slot, "process-a");
        transport.Announce(slot, "process-c");
        Assert.True(network.TryGetPeer(slot, out var peer));
        Assert.Equal("process-a", peer.PrincipalId);
        Assert.False(peer.IsAuthenticated);
        Assert.Equal(slot, peer.ConnectionId);

        var other = transport.OpenSlot();
        transport.Announce(other, "process-a");
        Assert.False(network.TryGetPeer(other, out _));
        Assert.True(network.TryGetPeer(slot, out var still));
        Assert.Equal("process-a", still.PrincipalId);
    }

    [Fact]
    public void Reconnect_keeps_one_writer_for_lww_and_sum()
    {
        var transport = new ScriptedTransport("process-b");
        var network = new NetworkManager(transport, allowUnauthenticated: true);
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        store.DefineField("offset", "sumContributions");
        var host = new AetherHost(store, 4096, 8);
        const string principal = "process-a";
        var slot = transport.AddPeer(principal);
        Assert.True(network.TryGetPeer(slot, out var peer));
        Assert.NotEqual(slot, peer.PrincipalId);
        Assert.False(peer.IsAuthenticated);

        var fields = RemoteFields(principal);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution(fields, peer.PrincipalId).Kind);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);
        Assert.Equal("kept", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(10, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(2, store.RecordCount);

        transport.Disconnect(principal);
        Assert.False(network.TryGetPeer(slot, out _));
        var again = transport.AddPeer(principal);
        Assert.NotEqual(slot, again);
        Assert.True(network.TryGetPeer(again, out var rebound));
        Assert.Equal(principal, rebound.PrincipalId);

        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution(fields, rebound.PrincipalId).Kind);
        Assert.Equal(AetherOutcomeKind.NoChange, Assert.Single(host.Drain()).Kind);
        Assert.Equal("kept", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(10, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(2, store.RecordCount);

        var pending = host.PendingCount;
        var clock = store.Clock;
        var forged = new StoredField(
            new RecordKey("entity-1", again, "offset"),
            FieldValue.FixedPoint(10),
            new FieldVersion(11, AetherActorId.Format(again, "s1")),
            again,
            "slot-op",
            1);
        Assert.Equal(AetherAdmitKind.Refused, host.TryAcceptContribution([forged], rebound.PrincipalId).Kind);
        Assert.Equal(pending, host.PendingCount);
        Assert.Equal(clock, store.Clock);
        Assert.Equal(10, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(2, store.RecordCount);
    }

    [Fact]
    public void Two_profiles_differ_and_one_profile_cannot_be_opened_twice()
    {
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        string leftId;
        using (var left = DurablePrincipal.Open(leftDir.Path))
        using (var right = DurablePrincipal.Open(rightDir.Path))
        {
            leftId = left.Id;
            Assert.NotEqual(left.Id, right.Id);
            var busy = Assert.Throws<IOException>(() => DurablePrincipal.Open(leftDir.Path));
            Assert.Equal("Profile is already open.", busy.Message);
        }

        using var reopened = DurablePrincipal.Open(leftDir.Path);
        Assert.Equal(leftId, reopened.Id);
    }

    [Fact]
    public void A_damaged_principal_file_does_not_open()
    {
        using var dir = new TempDir();
        using (var opened = DurablePrincipal.Open(dir.Path))
            Assert.Equal(64, opened.Id.Length);
        var path = Path.Combine(dir.Path, "principal");
        var bytes = File.ReadAllBytes(path);
        bytes[10] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        var flipped = Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
        Assert.Equal(DurablePrincipal.InvalidFile, flipped.Message);

        File.WriteAllBytes(path, new byte[DurablePrincipal.ByteLength]);
        var raw = Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
        Assert.Equal(DurablePrincipal.InvalidFile, raw.Message);
    }

    [Fact]
    public void A_restart_keeps_one_writer_after_the_process_id_changes()
    {
        using var identity = new TempDir();
        using var saved = new TempDir();
        string principal;
        using (var opened = DurablePrincipal.Open(identity.Path))
            principal = opened.Id;
        using (var reopened = DurablePrincipal.Open(identity.Path))
            Assert.Equal(principal, reopened.Id);
        Assert.Equal(64, principal.Length);
        Assert.NotEqual(PeerIdGenerator.Generate("peer", "peer"), PeerIdGenerator.Generate("peer", "peer"));

        var actor = AetherActorId.Format(principal, "s1");
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        store.DefineField("offset", "sumContributions");
        store.Apply(
            new AetherOperation(
                "pub",
                "entity-1",
                principal,
                actor,
                1,
                1,
                new Dictionary<string, FieldValue>(),
                AetherEffect.Publish,
                "catalog"),
            observeClock: true);
        store.RestoreActorSequence(actor, 1);
        using (var first = Link("instance-1", principal, "s1", store, 8, 64, 65_536))
        {
            Assert.Equal("instance-1", first.Transport.LocalPeerId);
            Assert.Equal(principal, first.Network.PrincipalId);
            Assert.NotEqual(first.Transport.LocalPeerId, first.Network.PrincipalId);
            Assert.Equal(AetherAdmitKind.Queued, first.Link.Submit("alpha").Kind);
            first.Link.Pump();
            var cursor = first.Store.ActorCursors.Single(item => item.ActorId == actor);
            first.Store.MergeContributions(
            [
                new StoredField(
                    new RecordKey("entity-1", principal, "offset"),
                    FieldValue.FixedPoint(10),
                    new FieldVersion(cursor.Lamport + 1, actor),
                    principal,
                    "sum-1",
                    cursor.Sequence + 1),
            ]);
            first.Host.Save(new AetherArchive(saved.Path));
        }

        string reloaded;
        using (var opened = DurablePrincipal.Open(identity.Path))
            reloaded = opened.Id;
        using var second = Link(
            "instance-2",
            reloaded,
            "s2",
            new AetherArchive(saved.Path).Load(),
            8,
            64,
            65_536);
        Assert.Equal(principal, second.Network.PrincipalId);
        Assert.Equal("instance-2", second.Transport.LocalPeerId);
        Assert.Equal("alpha", second.Store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(10, second.Store.ResolveSum("entity-1", "offset"));
        Assert.Equal(1, second.Store.Capture().Fields.Count(field => field.Key.FieldId == "offset"));
        Assert.DoesNotContain(second.Store.Capture().Fields, field => field.WriterId == "instance-2");

        second.Transport.AddPeer("remote");
        var sent = Assert.Single(second.Transport.Sent);
        Assert.True(AetherWire.TryDecodeContributions(sent, out var caughtUp, out _));
        var caughtLabel = Assert.Single(caughtUp);
        Assert.Equal(principal, caughtLabel.WriterId);
        Assert.Equal(actor, caughtLabel.Version.ActorId);
        Assert.Equal("alpha", caughtLabel.Value.Text);
        var hello = Assert.Single(second.Transport.Hellos);
        Assert.True(PeerHello.TryDecode(hello.Body, out var announced));
        Assert.Equal(principal, announced);
        Assert.NotEqual("instance-2", announced);

        var receiverTransport = new ScriptedTransport("instance-9");
        var receiverNetwork = new NetworkManager(receiverTransport, "other-principal", allowUnauthenticated: true);
        var receiverStore = new AetherArchive(saved.Path).Load();
        var receiverHost = new AetherHost(receiverStore, 4096, 8);
        var slot = receiverTransport.OpenSlot();
        receiverTransport.Announce(slot, principal);
        Assert.True(receiverNetwork.TryGetPeer(slot, out var bound));
        Assert.Equal(principal, bound.PrincipalId);
        Assert.NotEqual(slot, bound.ConnectionId is null ? slot : bound.PrincipalId);
        var owned = receiverStore.Capture().Fields.Where(field => field.WriterId == principal).ToArray();
        Assert.Equal(2, owned.Length);
        Assert.Equal(AetherAdmitKind.Queued, receiverHost.TryAcceptContribution(owned, bound.PrincipalId).Kind);
        Assert.Equal(AetherOutcomeKind.NoChange, Assert.Single(receiverHost.Drain()).Kind);
        Assert.Equal(10, receiverStore.ResolveSum("entity-1", "offset"));
        Assert.Equal(1, receiverStore.Capture().Fields.Count(field => field.Key.FieldId == "offset"));

        var forged = new StoredField(
            new RecordKey("entity-1", "instance-2", "offset"),
            FieldValue.FixedPoint(10),
            new FieldVersion(30, AetherActorId.Format("instance-2", "s2")),
            "instance-2",
            "instance-2/1",
            1);
        var pending = receiverHost.PendingCount;
        Assert.Equal(AetherAdmitKind.Refused, receiverHost.TryAcceptContribution([forged], bound.PrincipalId).Kind);
        Assert.Equal(pending, receiverHost.PendingCount);
        Assert.Equal(10, receiverStore.ResolveSum("entity-1", "offset"));
        Assert.Equal(1, receiverStore.Capture().Fields.Count(field => field.Key.FieldId == "offset"));
    }

    [Fact]
    public void An_unidentified_queue_is_bounded_by_bytes_and_an_oversized_packet_is_not_decoded()
    {
        var actor = AetherActorId.Format("peer-a", "s1");
        var packet = AetherWire.Encode(new AetherOperation(
            "peer-a/2",
            "entity-1",
            "peer-a",
            actor,
            2,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("kept") }));
        using var left = Peer("peer-a", maxUnidentifiedBytes: packet.Length);
        var slot = left.Transport.OpenSlot();
        left.Transport.DeliverSlot(slot, packet);
        left.Transport.DeliverSlot(slot, packet);
        Assert.Equal("Catch-up does not fit.", left.Link.LastRemoteFault);
        Assert.Equal(0, left.Host.PendingCount);

        left.Transport.DisconnectSlot(slot);
        var again = left.Transport.OpenSlot();
        var oversized = new byte[AetherWire.MaxPacketBytes + 1];
        oversized[0] = AetherWire.PacketType;
        left.Transport.DeliverSlot(again, oversized);
        Assert.Equal("State packet is too large.", left.Link.LastRemoteFault);
        Assert.Equal(0, left.Host.PendingCount);

        left.Transport.DeliverSlot(again, packet);
        Assert.Null(left.Link.LastRemoteFault);
        left.Transport.Announce(again, "peer-a");
        Assert.Equal(AetherAdmitKind.Queued, left.Link.LastRemoteAdmit!.Value.Kind);

        var transport = new ScriptedTransport("instance-3");
        var network = new NetworkManager(transport, "principal-3", allowUnauthenticated: true);
        var bare = transport.OpenSlot();
        var hugeHello = new byte[PeerHello.MaxBytes + 1];
        hugeHello[0] = PeerHello.Marker;
        hugeHello[1] = 1;
        transport.DeliverSlot(bare, hugeHello);
        Assert.False(network.TryGetPeer(bare, out _));
        transport.Announce(bare, "principal-3");
        Assert.True(network.TryGetPeer(bare, out var peer));
        Assert.Equal("principal-3", peer.PrincipalId);
        Assert.False(peer.IsAuthenticated);
    }

    [Fact]
    public void An_unidentified_packet_expires_before_it_is_admitted()
    {
        var time = new ManualTime();
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        using var left = Link(
            "local",
            "local",
            "s1",
            store,
            8,
            64,
            65_536,
            time: time,
            unidentifiedLifetime: TimeSpan.FromSeconds(5));
        var actor = AetherActorId.Format("peer-a", "s1");
        var packet = AetherWire.Encode(new AetherOperation(
            "peer-a/s1/1",
            "entity-1",
            "peer-a",
            actor,
            1,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("kept") }));
        var freshActor = AetherActorId.Format("peer-b", "s1");
        var freshPacket = AetherWire.Encode(new AetherOperation(
            "peer-b/s1/1",
            "entity-1",
            "peer-b",
            freshActor,
            1,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("now") }));
        var fresh = left.Transport.OpenSlot();
        left.Transport.DeliverSlot(fresh, freshPacket);
        left.Transport.Announce(fresh, "peer-b");
        Assert.Equal(AetherAdmitKind.Queued, left.Link.LastRemoteAdmit!.Value.Kind);

        var slot = left.Transport.OpenSlot();
        left.Transport.DeliverSlot(slot, packet);
        time.Now += TimeSpan.FromSeconds(6);
        var pending = left.Host.PendingCount;
        var clock = store.Clock;
        left.Transport.Announce(slot, "peer-a");
        Assert.Equal(pending, left.Host.PendingCount);
        Assert.Equal(clock, store.Clock);
        Assert.Equal(0, store.RecordCount);
    }

    [Fact]
    public void A_local_label_over_the_packet_limit_is_rejected_before_it_is_applied()
    {
        var huge = new string('x', 70_000);
        var operation = new AetherOperation(
            "too-big",
            "entity-1",
            "peer-a",
            AetherActorId.Format("peer-a", "s1"),
            1,
            1,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label(huge) });
        var encode = Assert.Throws<AetherProtocolException>(() => AetherWire.Encode(operation));
        Assert.Equal(AetherWire.PacketTooLarge, encode.Message);
        var contribution = Assert.Throws<AetherProtocolException>(() => AetherWire.EncodeContributions(
        [
            new StoredField(
                new RecordKey("entity-1", "peer-a", "label"),
                FieldValue.Label(huge),
                new FieldVersion(1, AetherActorId.Format("peer-a", "s1")),
                "peer-a",
                "too-big",
                1),
        ]));
        Assert.Equal(AetherWire.PacketTooLarge, contribution.Message);

        using var left = Peer("peer-a", maxBytes: 1_000_000);
        var clock = left.Store.Clock;
        var records = left.Store.RecordCount;
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit(huge).Kind);
        left.Link.Pump();
        Assert.Null(left.Label);
        Assert.Null(left.LinkHostLabel);
        Assert.Equal(clock, left.Store.Clock);
        Assert.Equal(records, left.Store.RecordCount);
        Assert.Empty(left.Transport.Sent);
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
        left.Transport.AddPeer("peer-b");
        left.Transport.Deliver("peer-b", stolen);
        Assert.Equal(AetherAdmitKind.Refused, left.Link.LastRemoteAdmit!.Value.Kind);
        left.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Equal("alpha", left.LinkHostLabel);

        Assert.Equal("alpha", LabelOf(Assert.Single(left.Transport.Sent)));
    }

    [Fact]
    public void Catch_up_blocked_by_a_full_outgoing_queue_resumes_on_the_next_pump()
    {
        using var left = Peer("peer-a", maxOutgoing: 1);
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
        left.Link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("beta").Kind);
        left.Store.RestoreActorSequence(AetherActorId.Format("peer-a", "s1"), long.MaxValue);

        left.Transport.AddPeer("peer-b");
        Assert.Empty(left.Transport.Sent);

        left.Link.Pump();
        Assert.Equal("alpha", left.Label);
        Assert.Equal("alpha", LabelOf(Assert.Single(left.Transport.Sent)));
    }

    [Fact]
    public void A_restart_sends_the_saved_label_and_both_peers_converge()
    {
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using (var left = Peer("peer-a"))
        using (var right = Peer("peer-b"))
        {
            Assert.Equal(AetherAdmitKind.Queued, left.Link.Submit("alpha").Kind);
            left.Link.Pump();
            Assert.Equal(AetherAdmitKind.Queued, right.Link.Submit("beta").Kind);
            right.Link.Pump();
            left.Host.Save(new AetherArchive(leftDir.Path));
            right.Host.Save(new AetherArchive(rightDir.Path));
        }

        using var leftAgain = Reload("peer-a", leftDir.Path);
        using var rightAgain = Reload("peer-b", rightDir.Path);
        leftAgain.Link.Pump();
        rightAgain.Link.Pump();
        Assert.Equal("alpha", leftAgain.Label);
        Assert.Equal("beta", rightAgain.Label);
        Assert.Empty(leftAgain.Transport.Sent);
        Assert.Empty(rightAgain.Transport.Sent);

        leftAgain.Transport.AddPeer(rightAgain.Transport.LocalPeerId);
        rightAgain.Transport.AddPeer(leftAgain.Transport.LocalPeerId);
        var alpha = Assert.Single(leftAgain.Transport.Sent);
        var beta = Assert.Single(rightAgain.Transport.Sent);
        Assert.Equal(AetherWire.ContributionType, alpha[0]);
        Assert.Equal(AetherWire.ContributionType, beta[0]);
        Assert.False(AetherWire.TryDecode(alpha, "entity-1", "label", out _, out _));
        Assert.True(AetherWire.TryDecodeContributions(alpha, out var leftFields, out _));
        Assert.True(AetherWire.TryDecodeContributions(beta, out var rightFields, out _));
        var leftField = Assert.Single(leftFields);
        var rightField = Assert.Single(rightFields);
        Assert.Equal(AetherActorId.Format("peer-a", "s1") + "/2", leftField.OperationId);
        Assert.Equal(2, leftField.Sequence);
        Assert.Equal("alpha", leftField.Value.Text);
        Assert.Equal(AetherActorId.Format("peer-b", "s1") + "/2", rightField.OperationId);
        Assert.Equal(2, rightField.Sequence);
        Assert.Equal("beta", rightField.Value.Text);

        rightAgain.Transport.Deliver(leftAgain.Transport.LocalPeerId, alpha);
        leftAgain.Transport.Deliver(rightAgain.Transport.LocalPeerId, beta);
        leftAgain.Link.Pump();
        rightAgain.Link.Pump();
        Assert.Equal("beta", leftAgain.Label);
        Assert.Equal("beta", rightAgain.Label);
        Assert.Equal("beta", leftAgain.LinkHostLabel);
        Assert.Equal("beta", rightAgain.LinkHostLabel);
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

    [Fact]
    public void Ownership_transfer_changes_the_peer_allowed_to_delete()
    {
        using var left = Peer("peer-a");
        using var right = Peer("peer-b");
        left.Transport.AddPeer(right.Transport.LocalPeerId);
        right.Transport.AddPeer(left.Transport.LocalPeerId);

        Assert.Equal(AetherAdmitKind.Queued, left.Link.SubmitCreate(TestStubEntity.RegistryTypeId).Kind);
        left.Link.Pump();
        right.Transport.Deliver(left.Transport.LocalPeerId, left.Transport.Sent[0]);
        right.Link.Pump();

        Assert.Equal(AetherAdmitKind.Queued, left.Link.SubmitOwnershipTransfer("peer-b").Kind);
        left.Link.Pump();
        right.Transport.Deliver(left.Transport.LocalPeerId, left.Transport.Sent[1]);
        right.Link.Pump();

        Assert.Equal("peer-b", left.Store.GetOwner("entity-1"));
        Assert.Equal("peer-b", right.Store.GetOwner("entity-1"));
        Assert.Equal("peer-b", Assert.IsAssignableFrom<Replica>(right.Scene.GetEntity("entity-1")).OwnerId);

        Assert.Equal(AetherAdmitKind.Queued, left.Link.SubmitDelete().Kind);
        left.Link.Pump();
        Assert.False(left.Store.IsDeleted("entity-1"));
        Assert.Equal(2, left.Transport.Sent.Count);

        Assert.Equal(AetherAdmitKind.Queued, right.Link.SubmitDelete().Kind);
        right.Link.Pump();
        var deletion = Assert.Single(right.Transport.Sent);
        left.Transport.Deliver(right.Transport.LocalPeerId, deletion);
        left.Link.Pump();

        Assert.True(left.Store.IsDeleted("entity-1"));
        Assert.True(right.Store.IsDeleted("entity-1"));
        Assert.Null(left.Scene.GetEntity("entity-1"));
        Assert.Null(right.Scene.GetEntity("entity-1"));
    }

    private static StoredField[] RemoteFields(string principal)
    {
        var actor = AetherActorId.Format(principal, "s1");
        return
        [
            new StoredField(
                new RecordKey("entity-1", principal, "label"),
                FieldValue.Label("kept"),
                new FieldVersion(10, actor),
                principal,
                "op-1",
                1),
            new StoredField(
                new RecordKey("entity-1", principal, "offset"),
                FieldValue.FixedPoint(10),
                new FieldVersion(10, actor),
                principal,
                "op-1",
                1),
        ];
    }

    private static string? LabelOf(byte[] packet)
    {
        Assert.True(AetherWire.TryDecode(packet, "entity-1", "label", out var operation, out _));
        return operation!.Changes["label"].Text;
    }

    private static PeerPeer Peer(string actor, int maxCount = 8, int maxOutgoing = 64, int maxUnidentifiedBytes = 65_536, int maxBytes = 4096)
    {
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        var actorId = AetherActorId.Format(actor, "s1");
        var published = new AetherOperation(
            "pub-" + actor,
            "entity-1",
            actor,
            actorId,
            1,
            1,
            new Dictionary<string, FieldValue>(),
            AetherEffect.Publish,
            "catalog");
        store.Apply(published, observeClock: true);
        store.RestoreActorSequence(actorId, 1);
        return Link(actor, actor, "s1", store, maxCount, maxOutgoing, maxUnidentifiedBytes, maxBytes);
    }

    private static PeerPeer Reload(string actor, string directory) =>
        Link(actor, actor, "s1", new AetherArchive(directory).Load(), 8, 64, 65_536);

    private static PeerPeer Link(
        string processId,
        string principal,
        string session,
        AetherReducer store,
        int maxCount,
        int maxOutgoing,
        int maxUnidentifiedBytes,
        int maxBytes = 4096,
        TimeProvider? time = null,
        TimeSpan? unidentifiedLifetime = null)
    {
        var transport = new ScriptedTransport(processId);
        var network = new NetworkManager(transport, principal, allowUnauthenticated: true);
        var sp = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(
            scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1"),
            log);
        var host = new AetherHost(store, maxBytes, maxCount);
        var link = new AetherLabelLink(
            network,
            host,
            scene,
            log,
            "entity-1",
            principal,
            session,
            "catalog",
            maxOutgoing: maxOutgoing,
            maxUnidentifiedBytes: maxUnidentifiedBytes,
            time: time,
            unidentifiedLifetime: unidentifiedLifetime,
            allowUnauthenticated: true);
        return new PeerPeer(transport, network, link, scene, store, host);
    }

    private sealed class PeerPeer : IDisposable
    {
        public PeerPeer(
            ScriptedTransport transport,
            NetworkManager network,
            AetherLabelLink link,
            ISceneManager scene,
            AetherReducer store,
            AetherHost host)
        {
            Transport = transport;
            Network = network;
            Link = link;
            Scene = scene;
            Store = store;
            Host = host;
        }

        public ScriptedTransport Transport { get; }

        public NetworkManager Network { get; }

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
        private int _nextSlot = 1;
        private readonly List<string> _peers = new();
        private readonly Dictionary<string, string> _slotByPrincipal = new(StringComparer.Ordinal);

        public ScriptedTransport(string localPeerId) => LocalPeerId = localPeerId;

        public string TransportType => "scripted";

        public string LocalPeerId { get; }

        public List<byte[]> Sent { get; } = new();

        public List<(string ConnectionId, byte[] Body)> Hellos { get; } = new();

        public int FailNextSends { get; set; }

        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[], int>? OnDataReceived;
        public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
        public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
        public event Action<string, Guid, int>? OnFileChunkRequested;

        public string AddPeer(string principal)
        {
            var slot = OpenSlot();
            _slotByPrincipal[principal] = slot;
            Announce(slot, principal);
            return slot;
        }

        public string OpenSlot()
        {
            var slot = _nextSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _nextSlot++;
            _peers.Add(slot);
            OnPeerConnected?.Invoke(slot);
            return slot;
        }

        public void Announce(string slot, string principal)
        {
            var hello = PeerHello.Encode(principal);
            OnDataReceived?.Invoke(slot, hello, hello.Length);
        }

        public void Disconnect(string principal)
        {
            if (!_slotByPrincipal.TryGetValue(principal, out var slot))
                return;
            _slotByPrincipal.Remove(principal);
            DisconnectSlot(slot);
        }

        public void DisconnectSlot(string slot)
        {
            _peers.Remove(slot);
            OnPeerDisconnected?.Invoke(slot);
        }

        public void Deliver(string principal, byte[] data)
        {
            var slot = _slotByPrincipal.TryGetValue(principal, out var known) ? known : principal;
            DeliverSlot(slot, data);
        }

        public void DeliverSlot(string slot, byte[] data) =>
            OnDataReceived?.Invoke(slot, data, data.Length);

        public void SendReliable(string peerId, byte[] data) => Take(peerId, data);

        public bool TrySendBulk(string peerId, byte[] data)
        {
            Take(peerId, data);
            return true;
        }

        public void SendUnreliable(string peerId, byte[] data) => Take(peerId, data);

        private void Take(string connectionId, byte[] data)
        {
            if (FailNextSends > 0)
            {
                FailNextSends--;
                throw new IOException("Send failed.");
            }

            if (PeerHello.IsHello(data))
            {
                Hellos.Add((connectionId, data.ToArray()));
                return;
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

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
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
