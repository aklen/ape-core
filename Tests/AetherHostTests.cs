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

        var refused = host.TryAcceptRemote(remote);
        Assert.Equal(AetherAdmitKind.Full, refused.Kind);
        Assert.Equal(0, refused.RequestId);
        Assert.Equal(0, host.PendingBytes);
        Assert.Equal(0, store.Clock);
        Assert.Equal(0, store.RecordCount);

        var room = new AetherHost(store, AetherHost.Measure(remote), 8);
        Assert.Equal(AetherAdmitKind.Queued, room.TryAcceptRemote(remote).Kind);
        var later = room.TryAcceptRemote(Remote("later"));
        Assert.Equal(AetherAdmitKind.Full, later.Kind);
        Assert.Equal(0, later.RequestId);
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
        var remoteId = host.TryAcceptRemote(remote);
        var localId = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("box"),
        });
        Assert.Equal(1, remoteId.RequestId);
        Assert.Equal(2, localId.RequestId);

        var logBefore = File.ReadAllBytes(archive.LogPath);
        var drained = host.Drain();
        Assert.Equal(logBefore, File.ReadAllBytes(archive.LogPath));
        Assert.Equal(2, drained.Count);
        Assert.All(drained, outcome => Assert.Equal(AetherOutcomeKind.Applied, outcome.Kind));
        Assert.Equal(12, store.Clock);
        Assert.Equal("box", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.ActorCursors.Single(cursor => cursor.ActorId == "peer-B/s1").Sequence);
        Assert.Equal(1, store.ActorCursors.Single(cursor => cursor.ActorId == "peer-A/s1").Sequence);

        var durable = host.Commit(archive);
        Assert.Equal(new long[] { 1, 2 }, durable.Select(item => item.RequestId));
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
        var queued = host.TryAcceptRemote(Remote("cube"));
        var drained = host.Drain();
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(drained).Kind);
        var durable = host.Save(archive);
        Assert.Equal(queued.RequestId, Assert.Single(durable).RequestId);
        Assert.Empty(host.Commit(archive));

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
        var first = byCount.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>());
        var second = byCount.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>());
        var third = byCount.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>());
        Assert.Equal(AetherAdmitKind.Queued, first.Kind);
        Assert.Equal(1, first.RequestId);
        Assert.Equal(2, second.RequestId);
        Assert.Equal(AetherAdmitKind.Full, third.Kind);
        Assert.Equal(0, third.RequestId);
        Assert.Equal(2, byCount.PendingCount);
        Assert.Equal(0, store.Clock);
    }

    [Fact]
    public void A_rejected_local_write_does_not_move_the_clock_and_the_next_write_still_drains()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.FixedPoint(4),
        }).Kind);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        }).Kind);

        var drained = host.Drain();
        host.Drain();
        Assert.Equal(AetherOutcomeKind.Rejected, drained[0].Kind);
        Assert.Equal("Field 'label' stores a label.", drained[0].Reason);
        Assert.Equal(AetherOutcomeKind.Applied, drained[1].Kind);

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
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(new AetherOperation(
            "bad",
            "entity-1",
            "peer-B",
            "peer-B/s1",
            0,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("nope") })).Kind);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(Remote("yes")).Kind);

        var drained = host.Drain();
        Assert.Equal("Actor sequence is missing.", drained[0].Reason);
        Assert.Equal(AetherOutcomeKind.Applied, drained[1].Kind);

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
        var rejected = host.TryAcceptLocal("entity-1", "peer-A", "", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("nope"),
        });
        var accepted = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        });

        var drained = host.Drain();
        host.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Equal(rejected.RequestId, drained[0].RequestId);
        Assert.Equal(AetherOutcomeKind.Rejected, drained[0].Kind);
        Assert.Equal("Actor sequence is missing.", drained[0].Reason);
        Assert.Equal(accepted.RequestId, drained[1].RequestId);
        Assert.Equal(AetherOutcomeKind.Applied, drained[1].Kind);
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
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(new AetherOperation(
            "bad",
            "entity-1",
            "peer-B",
            "peer-B/s1",
            1,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.FixedPoint(4) })).Kind);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(Remote("yes")).Kind);

        var drained = host.Drain();
        host.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Equal(AetherOutcomeKind.Rejected, drained[0].Kind);
        Assert.Equal("Field 'label' stores a label.", drained[0].Reason);
        Assert.Equal(AetherOutcomeKind.Applied, drained[1].Kind);
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
        var rejected = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("other"),
        });
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptLocal("entity-1", "peer-B", "peer-B/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        }).Kind);

        var drained = host.Drain();
        var again = host.Drain();

        Assert.Equal(AetherOutcomeKind.Rejected, drained[0].Kind);
        Assert.Equal(rejected.RequestId, drained[0].RequestId);
        Assert.Equal("Operation 'peer-A/s1/1' changed payload.", drained[0].Reason);
        Assert.Equal(AetherOutcomeKind.Applied, drained[1].Kind);
        Assert.Empty(again);
        Assert.Equal(12, store.Clock);
        Assert.Equal("ok", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("peer-B/s1", Assert.Single(store.ActorCursors).ActorId);
        Assert.Equal(1, store.ActorCursors[0].Sequence);
    }

    [Fact]
    public void A_queued_write_is_applied_before_it_is_durable_and_results_are_not_kept()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var host = new AetherHost(store, 4096, 1);
        var queued = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("box"),
        });
        var full = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("later"),
        });
        Assert.Equal(AetherAdmitKind.Queued, queued.Kind);
        Assert.Equal(1, queued.RequestId);
        Assert.Equal(AetherAdmitKind.Full, full.Kind);
        Assert.Equal(0, full.RequestId);

        var logBefore = File.ReadAllBytes(archive.LogPath);
        var drained = host.Drain();
        Assert.Equal(logBefore, File.ReadAllBytes(archive.LogPath));
        var applied = Assert.Single(drained);
        Assert.Equal(queued.RequestId, applied.RequestId);
        Assert.Equal(AetherOutcomeKind.Applied, applied.Kind);
        Assert.Null(applied.Reason);
        Assert.Empty(host.Drain());

        var durable = host.Commit(archive);
        Assert.Equal(queued.RequestId, Assert.Single(durable).RequestId);
        Assert.NotEqual(logBefore, File.ReadAllBytes(archive.LogPath));

        var next = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("next"),
        });
        Assert.Equal(2, next.RequestId);
    }

    [Fact]
    public void A_commit_that_stops_after_a_durable_write_keeps_that_request_id()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var host = new AetherHost(store, 4096, 8);
        var first = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("one"),
        });
        var second = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("two"),
        });
        host.Drain();

        var calls = 0;
        var disk = new IOException("disk full");
        var interrupted = Assert.Throws<AetherInterruptedException>(() => host.Commit(applied =>
        {
            if (calls++ > 0)
                throw disk;
            archive.Append(applied);
        }));

        Assert.Equal(first.RequestId, Assert.Single(interrupted.Durable).RequestId);
        Assert.Empty(interrupted.Outcomes);
        Assert.Same(disk, interrupted.InnerException);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AetherDurable>)interrupted.Durable)[0] = new AetherDurable(9));
        Assert.Equal(1, host.PendingCount);

        var rest = host.Commit(archive);
        Assert.Equal(second.RequestId, Assert.Single(rest).RequestId);
        var loaded = archive.Load();
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(2, loaded.Clock);

        var fresh = new AetherHost(NewStore(), 4096, 8);
        archive.Save(fresh.Reducer);
        Assert.Equal(AetherAdmitKind.Queued, fresh.TryAcceptRemote(Remote("cube")).Kind);
        fresh.Drain();
        var io = Assert.Throws<IOException>(() => fresh.Commit(_ => throw new IOException("disk full")));
        Assert.Equal("disk full", io.Message);
        Assert.Equal(1, fresh.PendingCount);
    }

    [Fact]
    public void A_drain_that_stops_after_a_success_keeps_that_outcome()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var first = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("one"),
        });
        var second = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("two"),
        });
        host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("three"),
        });

        var seen = 0;
        var interrupted = Assert.Throws<AetherInterruptedException>(() => host.Drain(requestId =>
        {
            if (seen++ == 1)
                throw new InvalidOperationException("fold broke");
            Assert.Equal(first.RequestId, requestId);
        }));

        var kept = Assert.Single(interrupted.Outcomes);
        Assert.Equal(first.RequestId, kept.RequestId);
        Assert.Equal(AetherOutcomeKind.Applied, kept.Kind);
        Assert.Empty(interrupted.Durable);
        Assert.IsType<InvalidOperationException>(interrupted.InnerException);
        Assert.Equal(1, store.Clock);
        Assert.Equal("one", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(3, host.PendingCount);

        var rest = host.Drain();
        Assert.Equal(second.RequestId, rest[0].RequestId);
        Assert.Equal(2, rest.Count);
        Assert.Equal(3, store.Clock);
        Assert.Equal("three", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void A_drain_retry_applies_a_stamp_that_already_moved_the_clock()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var first = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("one"),
        });
        var second = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("two"),
        });
        var stamps = 0;
        var broken = new InvalidOperationException("after stamp");
        var interrupted = Assert.Throws<AetherInterruptedException>(() => host.Drain(null, () =>
        {
            if (++stamps == 2)
                throw broken;
        }));

        Assert.Same(broken, interrupted.InnerException);
        Assert.Equal(first.RequestId, Assert.Single(interrupted.Outcomes).RequestId);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AetherOutcome>)interrupted.Outcomes)[0] = default);
        Assert.Equal(2, store.Clock);
        Assert.Equal(1, store.RecordCount);
        Assert.Equal(2, Assert.Single(store.ActorCursors).Sequence);

        var rest = host.Drain();
        Assert.Equal(second.RequestId, Assert.Single(rest).RequestId);
        Assert.Equal(2, store.Clock);
        Assert.Equal(1, store.RecordCount);
        Assert.Equal(2, Assert.Single(store.ActorCursors).Sequence);
        Assert.Equal("two", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void A_byte_budget_leaves_the_next_request_queued()
    {
        var store = NewStore();
        var first = Remote("one");
        var second = new AetherOperation(
            "peer-C/s1/two",
            "entity-1",
            "peer-C",
            "peer-C/s1",
            1,
            11,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("two") });
        var host = new AetherHost(store, 4096, 8);
        host.TryAcceptRemote(first);
        host.TryAcceptRemote(second);

        var drained = host.Drain(8, AetherHost.Measure(first));
        Assert.Equal(first.Id, Assert.Single(drained).Result!.Value.Operation.Id);
        Assert.Equal("one", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(11, store.Clock);
        Assert.Equal(2, host.PendingCount);

        var rest = host.Drain();
        Assert.Equal(second.Id, Assert.Single(rest).Result!.Value.Operation.Id);
        Assert.Equal("two", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(12, store.Clock);
    }

    [Fact]
    public void A_request_larger_than_the_byte_budget_still_drains_once()
    {
        var store = NewStore();
        var wide = Remote("wide-label-value");
        var next = new AetherOperation(
            "peer-C/s1/next",
            "entity-1",
            "peer-C",
            "peer-C/s1",
            1,
            11,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("next") });
        var host = new AetherHost(store, AetherHost.Measure(wide) + AetherHost.Measure(next), 8);
        host.TryAcceptRemote(wide);
        host.TryAcceptRemote(next);

        var drained = host.Drain(8, 1);
        Assert.Equal(wide.Id, Assert.Single(drained).Result!.Value.Operation.Id);
        Assert.Equal("wide-label-value", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(2, host.PendingCount);
    }

    [Fact]
    public void A_rejected_request_spends_the_operation_budget()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.FixedPoint(1),
        });
        var good = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        });

        var drained = host.Drain(1, 1_000_000);
        var rejected = Assert.Single(drained);
        Assert.Equal(AetherOutcomeKind.Rejected, rejected.Kind);
        Assert.Equal("Field 'label' stores a label.", rejected.Reason);
        Assert.Equal(0, store.Clock);
        Assert.Equal(0, store.RecordCount);
        Assert.Equal(1, host.PendingCount);

        var rest = host.Drain(1, 1_000_000);
        Assert.Equal(good.RequestId, Assert.Single(rest).RequestId);
        Assert.Equal("ok", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.Clock);
    }

    [Fact]
    public void A_zero_budget_is_refused_before_the_queue_moves()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        host.TryAcceptRemote(Remote("one"));

        Assert.Throws<ArgumentOutOfRangeException>(() => host.Drain(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Drain(1, 0));
        Assert.Equal(0, store.Clock);
        Assert.Equal(1, host.PendingCount);
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
