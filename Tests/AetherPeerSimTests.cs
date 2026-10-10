using Ape.Core.Aether;

namespace Ape.Core.Tests;

public sealed class AetherPeerSimTests
{
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

    [Fact]
    public void Reordered_and_duplicated_writes_resolve_to_the_same_view()
    {
        var labelA = Label("peer-A", 10, "cube");
        var labelB = Label("peer-B", 15, "box");
        var offsetA = Offset("peer-A", 12, 4);
        var offsetB = Offset("peer-B", 16, 7);
        Assert.Equal(1, labelA.Sequence);
        Assert.Equal(2, offsetA.Sequence);
        Assert.Equal(1, labelB.Sequence);
        Assert.Equal(2, offsetB.Sequence);
        var left = new[] { labelA, labelB, offsetA, offsetB, labelA, offsetB };
        var right = new[] { offsetB, offsetA, labelB, labelA, labelA, offsetB };

        var sim = new Sim();
        sim.Add("left");
        sim.Add("right");
        foreach (var op in left)
            sim.Deliver("left", op);
        foreach (var op in right)
            sim.Deliver("right", op);

        foreach (var id in new[] { "left", "right" })
        {
            var peer = sim.Peer(id);
            Assert.Equal("box", peer.ResolveLww("entity-1", "label")?.Text);
            Assert.Equal(11, peer.ResolveSum("entity-1", "offset"));
            Assert.Equal(4, peer.RawContribution("entity-1", "peer-A", "offset"));
            Assert.Equal(7, peer.RawContribution("entity-1", "peer-B", "offset"));
            Assert.Equal(4, peer.RecordCount);
            Assert.Equal(4, peer.DedupCount);
            Assert.True(peer.DedupCount <= peer.DedupCapacity);
            Assert.False(peer.IsDeleted("entity-1"));
        }
    }

    [Fact]
    public void A_disconnected_peer_keeps_deletion_and_withdraw_after_snapshot_catch_up()
    {
        var publish = Life(AetherEffect.Publish, 10, "catalog");
        var label = Label("peer-A", 11, "cube");
        var withdraw = Life(AetherEffect.WithdrawPublication, 20, "catalog");
        var delete = Life(AetherEffect.DeleteEntity, 21, null);
        Assert.Equal(1, publish.Sequence);
        Assert.Equal(2, label.Sequence);
        Assert.Equal(3, withdraw.Sequence);
        Assert.Equal(4, delete.Sequence);

        var sim = new Sim();
        sim.Add("ahead");
        sim.Add("behind");
        sim.Deliver("ahead", publish);
        sim.Deliver("behind", publish);
        sim.Deliver("ahead", label);
        sim.Deliver("behind", label);
        var stale = sim.Peer("behind").Capture();

        sim.Deliver("ahead", withdraw);
        sim.Deliver("ahead", delete);
        sim.CatchUp("behind", "ahead");

        var caughtUp = sim.Peer("behind");
        Assert.True(caughtUp.IsDeleted("entity-1"));
        Assert.False(caughtUp.IsMember("entity-1", "catalog"));
        Assert.Equal("cube", caughtUp.ResolveLww("entity-1", "label")?.Text);

        caughtUp.MergeImage(stale);
        Assert.True(caughtUp.IsDeleted("entity-1"));
        Assert.False(caughtUp.IsMember("entity-1", "catalog"));
        Assert.Equal("cube", caughtUp.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Restart_then_a_local_write_keeps_the_clock_and_actor_sequence()
    {
        using var dir = new TempDir();
        var sim = new Sim();
        var peer = sim.Add("peer-A", dir.Path);
        sim.Save("peer-A");

        var remote = sim.Deliver("peer-A", Label("peer-B", 10, "cube"));
        sim.Append("peer-A", remote);
        var stamped = peer.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("box"),
        });
        var local = peer.Apply(stamped, observeClock: false);
        sim.Append("peer-A", local);

        Assert.Equal(12, peer.Clock);
        var restarted = sim.Restart("peer-A");
        Assert.Equal(12, restarted.Clock);
        Assert.Equal("box", restarted.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(2, restarted.RecordCount);
        Assert.True(restarted.DedupCount <= restarted.DedupCapacity);

        var next = restarted.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("next"),
        });
        Assert.Equal(13, next.Lamport);
        Assert.Equal(1, remote.Operation.Sequence);
        Assert.Equal(stamped.Sequence + 1, next.Sequence);
    }

    [Fact]
    public void Partition_writes_merge_in_either_snapshot_order()
    {
        var publish = Life(AetherEffect.Publish, 10, "catalog");
        var withdraw = Life(AetherEffect.WithdrawPublication, 30, "catalog");
        var delete = Life(AetherEffect.DeleteEntity, 31, null);
        var offsetA = Offset("peer-A", 32, 4);
        var labelB = Label("peer-B", 15, "box");
        var offsetB = Offset("peer-B", 18, 9);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, new[] { publish.Sequence, withdraw.Sequence, delete.Sequence, offsetA.Sequence });
        Assert.Equal(new long[] { 1, 2 }, new[] { labelB.Sequence, offsetB.Sequence });

        var origin = Partition(publish, withdraw, delete, offsetA, labelB, offsetB);
        var imageA = origin.Peer("a").Capture();
        var imageB = origin.Peer("b").Capture();
        origin.Peer("a").MergeImage(imageB);
        origin.Peer("b").MergeImage(imageA);
        ExpectPartition(origin.Peer("a"));
        ExpectPartition(origin.Peer("b"));

        foreach (var aFirst in new[] { true, false })
        {
            var merged = new Sim();
            merged.Add("peer");
            if (aFirst)
            {
                merged.Peer("peer").MergeImage(imageA);
                merged.Peer("peer").MergeImage(imageB);
            }
            else
            {
                merged.Peer("peer").MergeImage(imageB);
                merged.Peer("peer").MergeImage(imageA);
            }

            ExpectPartition(merged.Peer("peer"));
        }
    }

    [Fact]
    public void Offline_writes_survive_merge_save_restart_and_a_new_write()
    {
        using var dir = new TempDir();
        var publish = Life(AetherEffect.Publish, 10, "catalog");
        var withdraw = Life(AetherEffect.WithdrawPublication, 30, "catalog");
        var delete = Life(AetherEffect.DeleteEntity, 31, null);
        var offsetA = Offset("peer-A", 32, 4);
        var labelB = Label("peer-B", 15, "box");
        var offsetB = Offset("peer-B", 18, 9);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, new[] { publish.Sequence, withdraw.Sequence, delete.Sequence, offsetA.Sequence });
        Assert.Equal(new long[] { 1, 2 }, new[] { labelB.Sequence, offsetB.Sequence });

        var sim = new Sim();
        var peer = sim.Add("a", dir.Path);
        sim.Add("b");
        sim.Deliver("a", publish);
        sim.Deliver("b", publish);
        sim.Deliver("a", withdraw);
        sim.Deliver("a", delete);
        sim.Deliver("a", offsetA);
        sim.Deliver("b", labelB);
        sim.Deliver("b", offsetB);
        sim.CatchUp("a", "b");
        sim.CatchUp("b", "a");
        ExpectPartition(sim.Peer("a"));
        ExpectPartition(sim.Peer("b"));
        Assert.Equal(33, peer.Clock);

        sim.Save("a");
        var restarted = sim.Restart("a");
        ExpectPartition(restarted);
        Assert.Equal(33, restarted.Clock);
        Assert.Equal(4, Assert.Single(restarted.ActorCursors).Sequence);

        var stamped = restarted.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("next"),
        });
        var applied = restarted.Apply(stamped, observeClock: false);
        Assert.Equal(34, stamped.Lamport);
        Assert.Equal(5, stamped.Sequence);
        sim.Append("a", applied);

        var saved = sim.Restart("a");
        Assert.Equal(34, saved.Clock);
        Assert.Equal("next", saved.ResolveLww("entity-1", "label")?.Text);
        Assert.True(saved.IsDeleted("entity-1"));
        Assert.False(saved.IsMember("entity-1", "catalog"));
        Assert.Equal(13, saved.ResolveSum("entity-1", "offset"));
        Assert.Equal(4, saved.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(9, saved.RawContribution("entity-1", "peer-B", "offset"));
        Assert.Equal(4, saved.RecordCount);
        Assert.True(saved.DedupCount <= saved.DedupCapacity);
        var followed = saved.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("after"),
        });
        Assert.Equal(35, followed.Lamport);
        Assert.Equal(6, followed.Sequence);
    }

    private static void ExpectPartition(AetherReducer peer)
    {
        Assert.True(peer.IsDeleted("entity-1"));
        Assert.False(peer.IsMember("entity-1", "catalog"));
        Assert.Equal("box", peer.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(13, peer.ResolveSum("entity-1", "offset"));
        Assert.Equal(4, peer.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(9, peer.RawContribution("entity-1", "peer-B", "offset"));
        Assert.Equal(3, peer.RecordCount);
        Assert.True(peer.DedupCount <= peer.DedupCapacity);
    }

    private Sim Partition(
        AetherOperation publish,
        AetherOperation withdraw,
        AetherOperation delete,
        AetherOperation offsetA,
        AetherOperation labelB,
        AetherOperation offsetB)
    {
        var sim = new Sim();
        sim.Add("a");
        sim.Add("b");
        sim.Deliver("a", publish);
        sim.Deliver("b", publish);
        sim.Deliver("a", withdraw);
        sim.Deliver("a", delete);
        sim.Deliver("a", offsetA);
        sim.Deliver("b", labelB);
        sim.Deliver("b", offsetB);
        return sim;
    }

    private AetherOperation Label(string writerId, long lamport, string label)
    {
        var (id, actorId, sequence) = Next(writerId);
        return new AetherOperation(id, "entity-1", writerId, actorId, sequence, lamport, new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label(label),
        });
    }

    private AetherOperation Offset(string writerId, long lamport, long value)
    {
        var (id, actorId, sequence) = Next(writerId);
        return new AetherOperation(id, "entity-1", writerId, actorId, sequence, lamport, new Dictionary<string, FieldValue>
        {
            ["offset"] = FieldValue.FixedPoint(value),
        });
    }

    private AetherOperation Life(AetherEffect effect, long lamport, string? publicationId)
    {
        var (id, actorId, sequence) = Next("peer-A");
        return new AetherOperation(id, "entity-1", "peer-A", actorId, sequence, lamport, new Dictionary<string, FieldValue>(), effect, publicationId);
    }

    private (string Id, string ActorId, long Sequence) Next(string writerId)
    {
        var actorId = $"{writerId}/s1";
        var sequence = _sequences.GetValueOrDefault(actorId) + 1;
        _sequences[actorId] = sequence;
        return ($"{actorId}/{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}", actorId, sequence);
    }

    private sealed class Sim
    {
        private readonly Dictionary<string, AetherReducer> _peers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AetherArchive> _archives = new(StringComparer.Ordinal);

        public AetherReducer Add(string id, string? directory = null)
        {
            var reducer = new AetherReducer(dedupCapacity: 8);
            reducer.DefineField("label", "lww");
            reducer.DefineField("offset", "sumContributions");
            _peers[id] = reducer;
            if (directory is not null)
                _archives[id] = new AetherArchive(directory);
            return reducer;
        }

        public AetherReducer Peer(string id) => _peers[id];

        public AetherApplied Deliver(string id, AetherOperation op)
        {
            var applied = _peers[id].Apply(op, observeClock: true);
            _peers[id].RestoreActorSequence(op.ActorId, op.Sequence);
            return applied;
        }

        public void CatchUp(string id, string source) =>
            _peers[id].MergeImage(_peers[source].Capture());

        public void Save(string id) => _archives[id].Save(_peers[id]);

        public void Append(string id, AetherApplied applied) => _archives[id].Append(applied);

        public AetherReducer Restart(string id)
        {
            var loaded = _archives[id].Load();
            _peers[id] = loaded;
            return loaded;
        }
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
}
