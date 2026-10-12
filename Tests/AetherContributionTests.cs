using Ape.Core.Aether;

namespace Ape.Core.Tests;

public sealed class AetherContributionTests
{
    [Fact]
    public void Restart_catch_up_merges_both_current_fields_without_replaying_the_old_operation()
    {
        using var dir = new TempDir();
        var batch = new AetherOperation(
            "batch",
            "entity-1",
            "peer-a",
            "peer-a",
            4,
            4,
            new Dictionary<string, FieldValue>
            {
                ["label"] = FieldValue.Label("alpha"),
                ["note"] = FieldValue.Label("one"),
            });
        var touch = new AetherOperation(
            "touch",
            "entity-1",
            "peer-a",
            "peer-a",
            5,
            6,
            new Dictionary<string, FieldValue> { ["note"] = FieldValue.Label("two") });

        var store = NewStore();
        store.Apply(batch, observeClock: true);
        store.Apply(touch, observeClock: true);
        new AetherArchive(dir.Path).Save(store);

        var loaded = new AetherArchive(dir.Path).Load();
        var saved = loaded.Capture().Fields;
        var savedLabel = Assert.Single(saved, field => field.Key.FieldId == "label");
        var savedNote = Assert.Single(saved, field => field.Key.FieldId == "note");
        Assert.Equal("batch", savedLabel.OperationId);
        Assert.Equal(4, savedLabel.Sequence);
        Assert.Equal("alpha", savedLabel.Value.Text);
        Assert.Equal("touch", savedNote.OperationId);
        Assert.Equal(5, savedNote.Sequence);
        Assert.Equal("two", savedNote.Value.Text);

        var packet = AetherWire.EncodeContributions(saved);
        Assert.Equal(AetherWire.ContributionType, packet[0]);
        Assert.False(AetherWire.TryDecode(packet, "entity-1", "label", out _, out _));
        Assert.True(AetherWire.TryDecodeContributions(packet, out var decoded, out _));

        var receiver = NewStore();
        receiver.MergeContributions(decoded);
        Assert.Equal("alpha", receiver.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("two", receiver.ResolveLww("entity-1", "note")?.Text);
        Assert.False(receiver.Remembers("batch"));
        Assert.False(receiver.Remembers("touch"));
        Assert.Equal(4, Assert.Single(receiver.Capture().Fields, field => field.Key.FieldId == "label").Sequence);
        Assert.Equal(5, Assert.Single(receiver.Capture().Fields, field => field.Key.FieldId == "note").Sequence);

        receiver.Apply(batch, observeClock: true);
        Assert.Equal("alpha", receiver.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("two", receiver.ResolveLww("entity-1", "note")?.Text);
        Assert.True(receiver.Remembers("batch"));

        var rebuilt = new AetherOperation(
            "batch",
            "entity-1",
            "peer-a",
            "peer-a",
            4,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("alpha") });
        var rejected = Assert.Throws<AetherProtocolException>(() => receiver.Apply(rebuilt, observeClock: true));
        Assert.Contains("changed payload", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("two", receiver.ResolveLww("entity-1", "note")?.Text);

        var next = receiver.StampLocal(
            "entity-1",
            "peer-a",
            "peer-a",
            new Dictionary<string, FieldValue> { ["note"] = FieldValue.Label("three") });
        Assert.Equal(6, next.Sequence);
    }

    [Fact]
    public void The_same_version_rejects_a_different_operation_id_or_sequence_in_either_order()
    {
        var first = Identity("op-1", 1);
        var second = Identity("op-2", 2);
        var forward = NewStore();
        var backward = NewStore();
        Assert.Throws<AetherProtocolException>(() => forward.MergeContributions([first, second]));
        Assert.Throws<AetherProtocolException>(() => backward.MergeContributions([second, first]));
        Assert.Equal(0, forward.RecordCount);
        Assert.Equal(0, backward.RecordCount);

        var keptFirst = NewStore();
        keptFirst.MergeContributions([first]);
        Assert.Throws<AetherProtocolException>(() => keptFirst.MergeContributions([second]));
        Assert.Equal("op-1", Assert.Single(keptFirst.Capture().Fields).OperationId);
        Assert.Equal(1, Assert.Single(keptFirst.Capture().Fields).Sequence);

        var keptSecond = NewStore();
        keptSecond.MergeContributions([second]);
        Assert.Throws<AetherProtocolException>(() => keptSecond.MergeContributions([first]));
        Assert.Equal("op-2", Assert.Single(keptSecond.Capture().Fields).OperationId);
        Assert.Equal(2, Assert.Single(keptSecond.Capture().Fields).Sequence);
    }

    [Fact]
    public void A_stale_contribution_does_not_move_the_actor_cursor()
    {
        var stored = NewStore();
        stored.MergeContributions([Label(10, "kept", sequence: 4)]);
        stored.MergeContributions([Label(5, "old", sequence: long.MaxValue)]);
        Assert.Equal("kept", stored.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(4, Assert.Single(stored.ActorCursors).Sequence);
        Assert.Equal(5, stored.StampLocal("entity-1", "peer-a", "peer-a", Note("next")).Sequence);

        foreach (var order in new[] { new[] { Stale(), Winner() }, new[] { Winner(), Stale() } })
        {
            var store = NewStore();
            store.MergeContributions(order);
            Assert.Equal("kept", store.ResolveLww("entity-1", "label")?.Text);
            Assert.Equal(4, Assert.Single(store.ActorCursors).Sequence);
            Assert.Equal(5, store.StampLocal("entity-1", "peer-a", "peer-a", Note("next")).Sequence);
        }
    }

    [Fact]
    public void An_unchanged_contribution_is_not_journaled_again()
    {
        using var dir = new TempDir();
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var archive = new AetherArchive(dir.Path);
        host.Save(archive);
        var row = Label(3, "caught-up", sequence: 2);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution([row]).Kind);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);
        var through = store.SaveThrough;
        var clock = store.Clock;

        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution([row]).Kind);
        var again = Assert.Single(host.Drain());
        Assert.Equal(AetherOutcomeKind.NoChange, again.Kind);
        Assert.Null(again.Result);
        Assert.Equal(through, store.SaveThrough);
        Assert.Equal(clock, store.Clock);
        Assert.Equal("caught-up", store.ResolveLww("entity-1", "label")?.Text);

        host.Commit(archive);
        var loaded = new AetherArchive(dir.Path).Load();
        Assert.Equal(through, loaded.SaveThrough);
        Assert.Equal(clock, loaded.Clock);
        Assert.Equal("caught-up", loaded.ResolveLww("entity-1", "label")?.Text);
    }

    private static StoredField Identity(string operationId, long sequence) =>
        new(
            new RecordKey("entity-1", "peer-a", "label"),
            FieldValue.Label("same"),
            new FieldVersion(10, "peer-a"),
            "peer-a",
            operationId,
            sequence);

    private static StoredField Winner() => Label(10, "kept", sequence: 4);

    private static StoredField Stale() => Label(5, "old", sequence: long.MaxValue);

    private static Dictionary<string, FieldValue> Note(string text) =>
        new() { ["note"] = FieldValue.Label(text) };

    [Fact]
    public void A_lower_version_collision_is_rejected_in_either_order()
    {
        var forward = NewStore();
        var backward = NewStore();
        var high = Label(20, "new");
        var left = Label(10, "x");
        var right = Label(10, "y");

        Assert.Throws<AetherProtocolException>(() => forward.MergeContributions([high, left, right]));
        Assert.Throws<AetherProtocolException>(() => backward.MergeContributions([left, right, high]));
        Assert.Equal(0, forward.RecordCount);
        Assert.Equal(0, backward.RecordCount);
        Assert.Equal(0, forward.Clock);
        Assert.Equal(0, backward.SaveThrough);

        var stored = NewStore();
        stored.MergeContributions([Label(10, "x", sequence: 2)]);
        var clock = stored.Clock;
        var saved = stored.SaveThrough;
        Assert.Throws<AetherProtocolException>(() => stored.MergeContributions([Label(20, "new", sequence: 3), Label(10, "y", sequence: 2)]));
        Assert.Throws<AetherProtocolException>(() => stored.MergeContributions([Label(10, "y", sequence: 2), Label(20, "new", sequence: 3)]));
        Assert.Equal("x", stored.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(clock, stored.Clock);
        Assert.Equal(saved, stored.SaveThrough);
    }

    [Fact]
    public void Commit_journals_a_contribution_merge_without_building_an_operation()
    {
        using var dir = new TempDir();
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var archive = new AetherArchive(dir.Path);
        host.Save(archive);
        var tiny = new AetherHost(NewStore(), 4096, 1);
        Assert.Equal(AetherAdmitKind.Queued, tiny.TryAcceptContribution([Label(3, "held")]).Kind);
        Assert.Equal(AetherAdmitKind.Full, tiny.TryAcceptContribution([Label(4, "second")]).Kind);
        Assert.Null(tiny.Reducer.ResolveLww("entity-1", "label")?.Text);

        var caughtUp = Label(3, "caught-up", sequence: 2);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution([caughtUp]).Kind);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution([Label(4, "later", sequence: 3)]).Kind);
        var drained = host.Drain(1, int.MaxValue);
        var outcome = Assert.Single(drained);
        Assert.Equal(AetherOutcomeKind.Applied, outcome.Kind);
        Assert.Null(outcome.Result!.Value.Operation);
        Assert.NotNull(outcome.Result.Value.Contributions);
        Assert.Equal(2, host.PendingCount);
        Assert.Equal("caught-up", store.ResolveLww("entity-1", "label")?.Text);
        Assert.False(store.Remembers("src"));

        host.Commit(archive);
        Assert.Equal(1, host.PendingCount);
        Assert.Equal("caught-up", store.ResolveLww("entity-1", "label")?.Text);
        var loaded = new AetherArchive(dir.Path).Load();
        Assert.Equal("caught-up", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.False(loaded.Remembers("src"));
        Assert.Equal(store.Clock, loaded.Clock);
    }

    private static StoredField Label(long lamport, string text, long sequence = 1) =>
        new(
            new RecordKey("entity-1", "peer-a", "label"),
            FieldValue.Label(text),
            new FieldVersion(lamport, "peer-a"),
            "peer-a",
            "src",
            sequence);

    private static AetherReducer NewStore()
    {
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        store.DefineField("note", "lww");
        return store;
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
