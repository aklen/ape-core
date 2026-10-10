using Ape.Core.Aether;

namespace Ape.Core.Tests;

public sealed class AetherArchiveTests
{
    [Fact]
    public void Load_restores_fields_deletion_and_one_withdrawn_publication()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore(sumMax: 150);
        Apply(store, LabelOp("label", "peer-A", "peer-A/s1", 1, 10, "cube"));
        Apply(store, FixedOp("a", "peer-A", "peer-A/s1", 2, 11, 100));
        Apply(store, FixedOp("b", "peer-B", "peer-B/s1", 1, 12, 100));
        Apply(store, Life(AetherEffect.Publish, "cat", 13, publicationId: "catalog"));
        Apply(store, Life(AetherEffect.Publish, "notes", 14, publicationId: "notes"));
        Apply(store, Life(AetherEffect.WithdrawPublication, "wd", 15, publicationId: "notes"));
        Apply(store, Life(AetherEffect.HideShared, "hide", 16));
        Apply(store, Life(AetherEffect.DeleteEntity, "delete", 17));
        var clock = store.Clock;
        archive.Save(store);

        Assert.Equal(clock, store.Clock);
        Assert.True(store.IsMember("entity-1", "catalog"));

        var live = NewStore();
        Apply(live, LabelOp("live", "peer-A", "peer-A/s1", 1, 40, "box"));
        var loaded = archive.Load();

        Assert.Equal("box", live.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("cube", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(150, loaded.ResolveSum("entity-1", "offset"));
        Assert.Equal(100, loaded.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(100, loaded.RawContribution("entity-1", "peer-B", "offset"));
        Assert.True(loaded.IsDeleted("entity-1"));
        Assert.False(loaded.IsSharedVisible("entity-1"));
        Assert.True(loaded.IsMember("entity-1", "catalog"));
        Assert.False(loaded.IsMember("entity-1", "notes"));
        Assert.Equal(clock, loaded.Clock);
    }

    [Fact]
    public void The_recovery_log_applies_only_operations_after_the_snapshot()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        var first = LabelOp("first", "peer-A", "peer-A/s1", 1, 10, "cube");
        Apply(store, first);
        archive.Save(store);
        archive.Append(store, first);

        var second = LabelOp("second", "peer-A", "peer-A/s1", 2, 20, "box");
        Apply(store, second);
        archive.Append(store, second);

        var loaded = archive.Load();
        Assert.Equal("box", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, loaded.Clock);
        Assert.Equal(1, loaded.RecordCount);
    }

    [Fact]
    public void An_older_log_generation_does_not_move_the_restored_clock()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore(dedupCapacity: 1);
        Apply(store, LabelOp("n1", "peer-A", "peer-A/s1", 1, 1, "v1"));
        Apply(store, LabelOp("n2", "peer-A", "peer-A/s1", 2, 2, "v2"));
        archive.Save(store);
        archive.Append(store, LabelOp("n1", "peer-A", "peer-A/s1", 1, 1, "v1"));
        var staleLog = File.ReadAllBytes(archive.LogPath);
        var clock = store.Clock;

        archive.Save(store);
        File.WriteAllBytes(archive.LogPath, staleLog);
        var loaded = archive.Load();

        Assert.Equal("v2", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(clock, loaded.Clock);
        Assert.False(loaded.Remembers("n1"));
    }

    [Fact]
    public void An_unfinished_snapshot_leaves_the_previous_file_in_place()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        Apply(store, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        archive.Save(store);

        Apply(store, LabelOp("new", "peer-A", "peer-A/s1", 2, 20, "box"));
        archive.Save(store, commitReplacement: false);

        Assert.True(File.Exists(archive.SnapshotPath + ".tmp"));
        var loaded = archive.Load();
        Assert.Equal("cube", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(11, loaded.Clock);
    }

    [Fact]
    public void A_truncated_snapshot_is_rejected()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        Apply(store, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        archive.Save(store);
        File.WriteAllBytes(archive.SnapshotPath, new byte[10]);

        Assert.Throws<AetherProtocolException>(() => archive.Load());
    }

    [Fact]
    public void A_bad_snapshot_checksum_is_rejected()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        Apply(store, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        archive.Save(store);
        var bytes = File.ReadAllBytes(archive.SnapshotPath);
        bytes[18] ^= 0xFF;
        File.WriteAllBytes(archive.SnapshotPath, bytes);

        Assert.Throws<AetherProtocolException>(() => archive.Load());
    }

    [Fact]
    public void A_torn_log_tail_is_dropped_and_a_bad_record_is_rejected()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        Apply(store, LabelOp("first", "peer-A", "peer-A/s1", 1, 10, "cube"));
        archive.Save(store);
        var second = LabelOp("second", "peer-A", "peer-A/s1", 2, 20, "box");
        Apply(store, second);
        archive.Append(store, second);
        var intact = File.ReadAllBytes(archive.LogPath);
        File.AppendAllBytes(archive.LogPath, [0, 0, 1, 0]);

        var loaded = archive.Load();
        Assert.Equal("box", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, loaded.Clock);

        intact[^1] ^= 0xFF;
        File.WriteAllBytes(archive.LogPath, intact);
        Assert.Throws<AetherProtocolException>(() => archive.Load());
    }

    [Fact]
    public void An_append_after_a_torn_tail_stays_reachable()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = LabelOp("one", "peer-A", "peer-A/s1", 1, 10, "one");
        Apply(store, one);
        archive.Append(store, one);
        File.AppendAllBytes(archive.LogPath, [0, 0, 1, 0]);

        var restored = archive.Load();
        Assert.Equal("one", restored.ResolveLww("entity-1", "label")?.Text);

        var two = LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two");
        Apply(store, two);
        archive.Append(store, two);
        var again = archive.Load();
        Assert.Equal("two", again.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, again.Clock);
    }

    [Fact]
    public void A_reopened_archive_repairs_a_torn_tail_before_append()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = LabelOp("one", "peer-A", "peer-A/s1", 1, 10, "one");
        Apply(store, one);
        archive.Append(store, one);
        File.AppendAllBytes(archive.LogPath, [0, 0, 1, 0]);

        var reopened = new AetherArchive(dir.Path);
        var two = LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two");
        Apply(store, two);
        reopened.Append(store, two);
        var loaded = reopened.Load();
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, loaded.Clock);
    }

    [Fact]
    public void An_append_after_a_new_snapshot_uses_the_new_log()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = LabelOp("one", "peer-A", "peer-A/s1", 1, 10, "one");
        Apply(store, one);
        archive.Append(store, one);

        archive.Save(store);
        var two = LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two");
        Apply(store, two);
        archive.Append(store, two);
        var loaded = archive.Load();
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, loaded.Clock);
    }

    [Fact]
    public void A_damaged_log_generation_is_rejected()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var kept = LabelOp("kept", "peer-A", "peer-A/s1", 1, 10, "kept");
        Apply(store, kept);
        archive.Append(store, kept);
        var bytes = File.ReadAllBytes(archive.LogPath);
        bytes[6] ^= 0xFF;
        File.WriteAllBytes(archive.LogPath, bytes);

        Assert.Throws<AetherProtocolException>(() => archive.Load());
    }

    [Fact]
    public void Local_stamps_reload_at_the_same_clock()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);

        var first = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("one"),
        });
        store.Apply(first, observeClock: false);
        archive.Append(store, first);
        var second = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("two"),
        });
        store.Apply(second, observeClock: false);
        archive.Append(store, second);

        Assert.Equal(2, store.Clock);
        var loaded = archive.Load();
        Assert.Equal(store.Clock, loaded.Clock);
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        var third = loaded.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("three"),
        });
        Assert.Equal(3, third.Lamport);
        Assert.Equal(second.Sequence + 1, third.Sequence);
    }

    [Fact]
    public void A_restored_actor_sequence_continues_after_the_saved_cursor()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        var first = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("cube"),
        });
        store.Apply(first, observeClock: false);
        archive.Save(store);

        var loaded = archive.Load();
        Assert.Equal(first.Lamport, loaded.Clock);
        var second = loaded.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("box"),
        });

        Assert.Equal(first.Lamport + 1, second.Lamport);
        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.True(second.Lamport > first.Lamport);
        Assert.Equal("cube", loaded.ResolveLww("entity-1", "label")?.Text);
    }

    private static AetherReducer NewStore(long sumMax = long.MaxValue, int dedupCapacity = AetherReducer.DefaultDedupCapacity)
    {
        var store = new AetherReducer(dedupCapacity);
        store.DefineField("label", "lww");
        store.DefineField("offset", "sumContributions", long.MinValue, sumMax);
        return store;
    }

    private static void Apply(AetherReducer store, AetherOperation op) => store.Apply(op, observeClock: true);

    private static AetherOperation LabelOp(string id, string writerId, string actorId, long sequence, long lamport, string label) =>
        new(id, "entity-1", writerId, actorId, sequence, lamport, new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label(label),
        });

    private static AetherOperation FixedOp(string id, string writerId, string actorId, long sequence, long lamport, long value) =>
        new(id, "entity-1", writerId, actorId, sequence, lamport, new Dictionary<string, FieldValue>
        {
            ["offset"] = FieldValue.FixedPoint(value),
        });

    private static AetherOperation Life(
        AetherEffect effect,
        string id,
        long lamport,
        string? publicationId = null) =>
        new(id, "entity-1", "peer-A", "peer-A/s1", 1, lamport, new Dictionary<string, FieldValue>(), effect, publicationId);

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
