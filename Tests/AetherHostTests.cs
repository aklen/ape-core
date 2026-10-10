using Ape.Core.Aether;
using Ape.Core.Graph;

namespace Ape.Core.Tests;

public sealed class AetherHostTests
{
    [Fact]
    public void A_submission_past_the_byte_cap_leaves_the_reducer_unchanged()
    {
        var store = NewStore();
        var remote = Remote("cube");
        var host = new AetherHost(store, AetherHost.Measure(remote) - 1, 8);

        Assert.False(host.TryAcceptRemote(remote));
        Assert.Equal(0, host.PendingBytes);
        Assert.Equal(0, store.Clock);
        Assert.Equal(0, store.RecordCount);

        var room = new AetherHost(store, AetherHost.Measure(remote), 8);
        Assert.True(room.TryAcceptRemote(remote));
        Assert.False(room.TryAcceptRemote(Remote("later")));
        Assert.Equal(AetherHost.Measure(remote), room.PendingBytes);
        Assert.Equal(0, store.Clock);
    }

    [Fact]
    public void Local_and_remote_writes_drain_together_and_commit_outside_the_tick()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var remote = Remote("cube");
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptRemote(remote));
        Assert.True(host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("box"),
        }));

        var logBefore = File.ReadAllBytes(archive.LogPath);
        var drained = host.Drain();
        Assert.Equal(logBefore, File.ReadAllBytes(archive.LogPath));
        Assert.Equal(2, drained.Count);
        Assert.Equal(12, store.Clock);
        Assert.Equal("box", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.ActorCursors.Single(cursor => cursor.ActorId == "peer-B/s1").Sequence);
        Assert.Equal(1, store.ActorCursors.Single(cursor => cursor.ActorId == "peer-A/s1").Sequence);

        host.Commit(archive);
        Assert.Equal(0, host.PendingBytes);
        var loaded = archive.Load();
        Assert.Equal(12, loaded.Clock);
        Assert.Equal("box", loaded.ResolveLww("entity-1", "label")?.Text);
        var next = loaded.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("next"),
        });
        Assert.Equal(13, next.Lamport);
        Assert.Equal(2, next.Sequence);
    }

    [Fact]
    public void A_save_outside_the_tick_keeps_drained_writes_without_a_log_record()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptRemote(Remote("cube")));
        host.Drain();
        host.Save(archive);

        var loaded = archive.Load();
        Assert.Equal("cube", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(11, loaded.Clock);
        Assert.Equal(1, Assert.Single(loaded.ActorCursors).Sequence);
    }

    [Fact]
    public void Empty_local_requests_cannot_bypass_the_byte_or_count_cap()
    {
        var store = NewStore();
        var byBytes = new AetherHost(store, maxBytes: 1, maxCount: 10_000);
        for (var i = 0; i < 10_000; i++)
            byBytes.TryAcceptLocal("", "", "", new Dictionary<string, FieldValue>());

        Assert.Equal(1, byBytes.PendingCount);
        Assert.True(byBytes.PendingBytes >= 1);
        Assert.Equal(0, store.Clock);

        var byCount = new AetherHost(store, maxBytes: 100_000, maxCount: 2);
        Assert.True(byCount.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>()));
        Assert.True(byCount.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>()));
        Assert.False(byCount.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>()));
        Assert.Equal(2, byCount.PendingCount);
        Assert.Equal(0, store.Clock);
    }

    [Fact]
    public void A_rejected_local_write_does_not_move_the_clock_and_the_next_write_still_drains()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.FixedPoint(4),
        }));
        Assert.True(host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        }));

        host.Drain();
        host.Drain();

        Assert.Equal(1, store.Clock);
        Assert.Equal(1, Assert.Single(store.ActorCursors).Sequence);
        Assert.Equal("ok", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void A_remote_operation_with_no_actor_sequence_is_dropped_before_apply()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptRemote(new AetherOperation(
            "bad",
            "entity-1",
            "peer-B",
            "peer-B/s1",
            0,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("nope") })));
        Assert.True(host.TryAcceptRemote(Remote("yes")));

        host.Drain();

        Assert.Equal(11, store.Clock);
        Assert.Equal("yes", store.ResolveLww("entity-1", "label")?.Text);
        Assert.False(store.Remembers("bad"));
        Assert.Equal(1, Assert.Single(store.ActorCursors).Sequence);

        var viaPlan = NewStore();
        var plan = AetherFrame.Compile();
        var scratch = new AetherScratch
        {
            Reducer = viaPlan,
            Inbox =
            [
                new AetherOperation("bad", "entity-1", "peer-B", "peer-B/s1", 0, 10, new Dictionary<string, FieldValue>
                {
                    ["label"] = FieldValue.Label("nope"),
                }),
                Remote("yes"),
            ],
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = "catalog",
        };
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        plan.Tick(ref scratch, ref runtime);

        Assert.Equal(11, viaPlan.Clock);
        Assert.Equal("yes", viaPlan.ResolveLww("entity-1", "label")?.Text);
        Assert.False(viaPlan.Remembers("bad"));
    }

    [Fact]
    public void An_empty_local_actor_is_dropped_before_stamping_and_the_next_write_drains()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptLocal("entity-1", "peer-A", "", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("nope"),
        }));
        Assert.True(host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        }));

        var drained = host.Drain();
        host.Drain();

        Assert.Single(drained);
        Assert.Equal(1, host.PendingCount);
        Assert.Equal(1, store.Clock);
        Assert.Equal("ok", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.RecordCount);
        var cursor = Assert.Single(store.ActorCursors);
        Assert.Equal("peer-A/s1", cursor.ActorId);
        Assert.Equal(1, cursor.Sequence);
    }

    [Fact]
    public void A_remote_field_of_the_wrong_type_is_dropped_and_the_next_write_drains()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptRemote(new AetherOperation(
            "bad",
            "entity-1",
            "peer-B",
            "peer-B/s1",
            1,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.FixedPoint(4) })));
        Assert.True(host.TryAcceptRemote(Remote("yes")));

        var drained = host.Drain();
        host.Drain();

        Assert.Single(drained);
        Assert.Equal(1, host.PendingCount);
        Assert.Equal(11, store.Clock);
        Assert.Equal("yes", store.ResolveLww("entity-1", "label")?.Text);
        Assert.False(store.Remembers("bad"));
        Assert.Equal(1, Assert.Single(store.ActorCursors).Sequence);

        var viaPlan = NewStore();
        var plan = AetherFrame.Compile();
        var scratch = new AetherScratch
        {
            Reducer = viaPlan,
            Inbox =
            [
                new AetherOperation("bad", "entity-1", "peer-B", "peer-B/s1", 1, 10, new Dictionary<string, FieldValue>
                {
                    ["label"] = FieldValue.FixedPoint(4),
                }),
                Remote("yes"),
            ],
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = "catalog",
        };
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        plan.Tick(ref scratch, ref runtime);

        Assert.Empty(scratch.Inbox);
        Assert.Equal(11, viaPlan.Clock);
        Assert.Equal("yes", viaPlan.ResolveLww("entity-1", "label")?.Text);
        Assert.False(viaPlan.Remembers("bad"));
    }

    [Fact]
    public void A_local_stamp_rejected_after_admission_checks_does_not_move_the_clock()
    {
        var store = NewStore();
        store.Apply(new AetherOperation(
            "peer-A/s1/1",
            "entity-1",
            "peer-A",
            "peer-A/s1",
            1,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("taken") }), observeClock: true);
        var host = new AetherHost(store, 4096, 8);
        Assert.True(host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("other"),
        }));
        Assert.True(host.TryAcceptLocal("entity-1", "peer-B", "peer-B/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        }));

        var drained = host.Drain();
        host.Drain();

        Assert.Single(drained);
        Assert.Equal(12, store.Clock);
        Assert.Equal("ok", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("peer-B/s1", Assert.Single(store.ActorCursors).ActorId);
        Assert.Equal(1, store.ActorCursors[0].Sequence);
    }

    private static AetherReducer NewStore()
    {
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        return store;
    }

    private static AetherOperation Remote(string label) =>
        new($"peer-B/s1/{label}", "entity-1", "peer-B", "peer-B/s1", 1, 10, new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label(label),
        });

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
