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
        var firstApplied = Apply(store, first);
        archive.Save(store);
        archive.Append(firstApplied);

        var second = LabelOp("second", "peer-A", "peer-A/s1", 2, 20, "box");
        archive.Append(Apply(store, second));

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
        archive.Save(store);
        var stale = Apply(store, LabelOp("n1", "peer-A", "peer-A/s1", 1, 1, "v1"));
        archive.Append(stale);
        Apply(store, LabelOp("n2", "peer-A", "peer-A/s1", 2, 2, "v2"));
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
        archive.Append(Apply(store, second));
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
        archive.Append(Apply(store, one));
        File.AppendAllBytes(archive.LogPath, [0, 0, 1, 0]);

        var restored = archive.Load();
        Assert.Equal("one", restored.ResolveLww("entity-1", "label")?.Text);

        var two = LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two");
        archive.Append(Apply(store, two));
        var again = archive.Load();
        Assert.Equal("two", again.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, again.Clock);
    }

    [Fact]
    public void A_complete_record_is_not_durable_until_repair_flushes_it()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = Apply(store, LabelOp("one", "peer-A", "peer-A/s1", 1, 10, "one"));
        var two = Apply(store, LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two"));

        archive.FailNextFlushes(1);
        Assert.Throws<IOException>(() => archive.Append(one));
        var written = File.ReadAllBytes(archive.LogPath);

        archive.FailNextFlushes(1);
        Assert.Throws<IOException>(() => archive.Append(one));
        Assert.Equal(written, File.ReadAllBytes(archive.LogPath));

        archive.Append(one);
        Assert.Equal(written, File.ReadAllBytes(archive.LogPath));
        archive.Append(two);
        var loaded = archive.Load();
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, loaded.Clock);
    }

    [Fact]
    public void A_finished_record_left_by_a_failed_append_is_not_written_again()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = Apply(store, LabelOp("one", "peer-A", "peer-A/s1", 1, 10, "one"));
        archive.Append(one);
        var two = Apply(store, LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two"));
        new AetherArchive(dir.Path).Append(two);
        var bytes = File.ReadAllBytes(archive.LogPath);

        archive.Append(two);

        Assert.Equal(bytes, File.ReadAllBytes(archive.LogPath));
        var loaded = archive.Load();
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(store.Clock, loaded.Clock);
    }

    [Fact]
    public void A_reopened_archive_repairs_a_torn_tail_before_append()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = LabelOp("one", "peer-A", "peer-A/s1", 1, 10, "one");
        archive.Append(Apply(store, one));
        File.AppendAllBytes(archive.LogPath, [0, 0, 1, 0]);

        var reopened = new AetherArchive(dir.Path);
        var two = LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two");
        reopened.Append(Apply(store, two));
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
        archive.Append(Apply(store, one));

        archive.Save(store);
        var two = LabelOp("two", "peer-A", "peer-A/s1", 2, 20, "two");
        archive.Append(Apply(store, two));
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
        archive.Append(Apply(store, kept));
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
        var firstApplied = store.Apply(first, observeClock: false);
        var second = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("two"),
        });
        var secondApplied = store.Apply(second, observeClock: false);
        archive.Append(firstApplied);
        archive.Append(secondApplied);

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

    [Fact]
    public void A_later_write_does_not_replace_the_logged_clock()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);

        var first = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("one"),
        });
        var applied = store.Apply(first, observeClock: false);
        var second = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("two"),
        });
        store.Apply(second, observeClock: false);
        archive.Append(applied);

        var loaded = archive.Load();
        Assert.Equal(1, applied.Clock);
        Assert.Equal(2, store.Clock);
        Assert.Equal(applied.Clock, loaded.Clock);
        Assert.Equal("one", loaded.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void A_covered_application_is_left_out_of_the_new_log()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        var one = Apply(store, LabelOp("one", "peer-A", "peer-A/s1", 1, 50, "one"));
        var two = Apply(store, LabelOp("two", "peer-A", "peer-A/s1", 2, 40, "two"));
        Assert.Equal(51, one.Clock);
        Assert.Equal(52, two.Clock);
        Assert.True(one.SaveSequence < two.SaveSequence);
        archive.Save(store);
        archive.Append(one);

        var loaded = archive.Load();
        Assert.Equal(52, loaded.Clock);
        Assert.Equal("one", loaded.ResolveLww("entity-1", "label")?.Text);

        var three = Apply(store, FixedOp("three", "peer-B", "peer-B/s1", 1, 1, 9));
        Assert.True(three.Operation.Lamport < one.Operation.Lamport);
        Assert.True(three.SaveSequence > two.SaveSequence);
        archive.Append(three);
        var again = archive.Load();
        Assert.Equal(store.Clock, again.Clock);
        Assert.Equal("one", again.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(9, again.RawContribution("entity-1", "peer-B", "offset"));
    }

    [Fact]
    public void An_out_of_order_append_is_rejected_before_it_changes_the_log()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var one = Apply(store, LabelOp("one", "peer-A", "peer-A/s1", 1, 0, "one"));
        var two = Apply(store, LabelOp("two", "peer-A", "peer-A/s1", 2, 1, "two"));
        Assert.Equal(1, one.SaveSequence);
        Assert.Equal(2, two.SaveSequence);
        var before = File.ReadAllBytes(archive.LogPath);

        Assert.Throws<AetherProtocolException>(() => archive.Append(two));
        Assert.Equal(before, File.ReadAllBytes(archive.LogPath));
        var loaded = archive.Load();
        Assert.Equal(0, loaded.Clock);
        Assert.Null(loaded.ResolveLww("entity-1", "label")?.Text);

        archive.Append(one);
        archive.Append(two);
        var again = archive.Load();
        Assert.Equal(2, again.Clock);
        Assert.Equal("two", again.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void A_previous_archive_format_is_rejected()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var bytes = new byte[82];
        "AET1"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(6), 1);
        System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, 18)).CopyTo(bytes.AsSpan(18));
        System.Security.Cryptography.SHA256.HashData(ReadOnlySpan<byte>.Empty).CopyTo(bytes.AsSpan(50));
        File.WriteAllBytes(archive.SnapshotPath, bytes);

        var error = Assert.Throws<AetherProtocolException>(() => archive.Load());
        Assert.Contains("format 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_previous_log_format_is_rejected()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        archive.Save(NewStore());
        var bytes = new byte[82];
        "AEL1"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(6), 1);
        System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, 18)).CopyTo(bytes.AsSpan(18));
        System.Security.Cryptography.SHA256.HashData(ReadOnlySpan<byte>.Empty).CopyTo(bytes.AsSpan(50));
        File.WriteAllBytes(archive.LogPath, bytes);

        var error = Assert.Throws<AetherProtocolException>(() => archive.Load());
        Assert.Contains("format 1", error.Message, StringComparison.Ordinal);
    }

    private static AetherApplied Apply(AetherReducer store, AetherOperation op) => store.Apply(op, observeClock: true);

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
