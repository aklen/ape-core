using Ape.Core.Aether;
using Ape.Core.Graph;

namespace Ape.Core.Tests;

public sealed class AetherReducerTests
{
    [Fact]
    public void Different_fields_from_two_writers_both_remain_in_either_order()
    {
        var forward = NewStore();
        Apply(forward, LabelOp("a-label", "peer-A", "peer-A/s1", 1, 10, "cube"));
        Apply(forward, FixedOp("b-offset", "peer-B", "peer-B/s1", 1, 11, 2));

        var backward = NewStore();
        Apply(backward, FixedOp("b-offset", "peer-B", "peer-B/s1", 1, 11, 2));
        Apply(backward, LabelOp("a-label", "peer-A", "peer-A/s1", 1, 10, "cube"));

        Assert.Equal("cube", forward.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(2, forward.ResolveSum("entity-1", "offset"));
        Assert.Equal("cube", backward.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(2, backward.ResolveSum("entity-1", "offset"));
        Assert.Equal(2, forward.RecordCount);
    }

    [Fact]
    public void Higher_lamport_wins_the_same_field_in_either_order()
    {
        foreach (var ids in new[] { new[] { "low", "high" }, new[] { "high", "low" } })
        {
            var store = NewStore();
            foreach (var id in ids)
            {
                if (id == "low")
                    Apply(store, LabelOp("low", "peer-A", "peer-A/s1", 1, 10, "cube"));
                else
                    Apply(store, LabelOp("high", "peer-B", "peer-B/s1", 1, 15, "box"));
            }

            Assert.Equal("box", store.ResolveLww("entity-1", "label")?.Text);
        }
    }

    [Fact]
    public void Equal_lamport_keeps_the_greater_actor_id()
    {
        var store = NewStore();
        Apply(store, LabelOp("a", "peer-A", "peer-A/s1", 1, 10, "cube"));
        Apply(store, LabelOp("b", "peer-B", "peer-B/s1", 1, 10, "box"));

        Assert.Equal("box", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Duplicate_sum_update_keeps_one_contribution()
    {
        var store = NewStore();
        var op = FixedOp("b", "peer-B", "peer-B/s1", 1, 4, 3);
        Apply(store, op);
        Apply(store, op);

        Assert.Equal(1, store.RecordCount);
        Assert.Equal(3, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(3, store.RawContribution("entity-1", "peer-B", "offset"));
        Assert.Equal(5, store.Clock);
    }

    [Fact]
    public void A_writer_replaces_their_own_sum_contribution()
    {
        var store = NewStore();
        Apply(store, FixedOp("a1", "peer-A", "peer-A/s1", 1, 1, 2));
        Apply(store, FixedOp("b1", "peer-B", "peer-B/s1", 1, 2, 3));
        Apply(store, FixedOp("b2", "peer-B", "peer-B/s2", 1, 3, 4));

        Assert.Equal(2, store.RecordCount);
        Assert.Equal(6, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(4, store.RawContribution("entity-1", "peer-B", "offset"));
    }

    [Fact]
    public void Snapshot_merge_keeps_the_newer_field_value()
    {
        var origin = NewStore();
        Apply(origin, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        var image = origin.Capture();
        Apply(origin, LabelOp("new", "peer-A", "peer-A/s1", 2, 20, "box"));

        var imageThenLive = NewStore();
        imageThenLive.MergeImage(image);
        Apply(imageThenLive, LabelOp("new", "peer-A", "peer-A/s1", 2, 20, "box"));

        var liveThenImage = NewStore();
        Apply(liveThenImage, LabelOp("new", "peer-A", "peer-A/s1", 2, 20, "box"));
        liveThenImage.MergeImage(image);

        Assert.Equal("box", imageThenLive.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("box", liveThenImage.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, imageThenLive.RecordCount);
        Assert.Equal(1, liveThenImage.RecordCount);
    }

    [Fact]
    public void A_newer_snapshot_beats_an_older_live_value_in_either_order()
    {
        var newer = NewStore();
        Apply(newer, LabelOp("new", "peer-A", "peer-A/s1", 2, 20, "box"));
        var image = newer.Capture();

        var imageThenOld = NewStore();
        imageThenOld.MergeImage(image);
        Apply(imageThenOld, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));

        var oldThenImage = NewStore();
        Apply(oldThenImage, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        oldThenImage.MergeImage(image);

        Assert.Equal("box", imageThenOld.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal("box", oldThenImage.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void A_merged_snapshot_still_rejects_a_reused_operation_id()
    {
        var origin = NewStore();
        Apply(origin, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        var image = origin.Capture();

        var restored = NewStore();
        restored.MergeImage(image);

        Assert.Throws<AetherProtocolException>(() =>
            Apply(restored, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "box")));
        Assert.Equal("cube", restored.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Repeated_updates_do_not_grow_the_store()
    {
        var store = NewStore();
        for (var i = 1; i <= 50; i++)
            Apply(store, LabelOp($"n{i}", "peer-A", "peer-A/s1", i, i, $"v{i}"));

        Assert.Equal(1, store.RecordCount);
        Assert.Equal("v50", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Saturated_sum_leaves_raw_contributions_unchanged()
    {
        var store = NewStore(sumMax: 150);
        Apply(store, FixedOp("a", "peer-A", "peer-A/s1", 1, 1, 100));
        Apply(store, FixedOp("b", "peer-B", "peer-B/s1", 1, 2, 100));

        Assert.Equal(150, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(100, store.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(100, store.RawContribution("entity-1", "peer-B", "offset"));
    }

    [Fact]
    public void Seeing_a_remote_write_makes_the_next_local_clock_strictly_greater()
    {
        var store = NewStore();
        Apply(store, LabelOp("remote", "peer-B", "peer-B/s1", 1, 10, "box"));
        var local = store.StampLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("cube"),
        });

        Assert.True(local.Lamport > 10);
    }

    [Fact]
    public void Same_operation_id_with_a_different_payload_is_rejected()
    {
        var store = NewStore();
        Apply(store, LabelOp("same", "peer-A", "peer-A/s1", 1, 10, "cube"));

        var changed = LabelOp("same", "peer-A", "peer-A/s1", 1, 10, "box");
        Assert.Throws<AetherProtocolException>(() => Apply(store, changed));
        Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void Unknown_resolver_is_rejected()
    {
        var store = new AetherReducer();
        Assert.Throws<AetherProtocolException>(() => store.DefineField("body", "keepConflicts"));
        Assert.Equal(0, store.RecordCount);
    }

    [Fact]
    public void A_remembered_digest_is_not_a_replayable_payload()
    {
        var store = NewStore();
        Apply(store, LabelOp("n1", "peer-A", "peer-A/s1", 1, 1, "cube"));

        Assert.True(store.Remembers("n1"));
        Assert.False(store.CanReplay("n1"));
        Assert.False(store.CanReplay("missing"));
    }

    [Fact]
    public void Dedup_window_stays_bounded_without_closing_unseen_operations()
    {
        var store = NewStore(dedupCapacity: 4);
        for (var i = 1; i <= 20; i++)
            Apply(store, LabelOp($"n{i}", "peer-A", "peer-A/s1", i, i, $"v{i}"));

        Assert.Equal(4, store.DedupCount);
        Assert.Equal(1, store.RecordCount);
        Assert.Equal("v20", store.ResolveLww("entity-1", "label")?.Text);
        Assert.False(store.Remembers("n1"));

        Apply(store, LabelOp("n1", "peer-A", "peer-A/s1", 1, 1, "v1"));
        Assert.Equal("v20", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void An_evicted_unchanged_sum_replay_keeps_the_contribution_and_the_total()
    {
        var store = NewStore(dedupCapacity: 1);
        var first = FixedOp("a", "peer-A", "peer-A/s1", 1, 1, 2);
        Apply(store, first);
        Apply(store, FixedOp("b", "peer-B", "peer-B/s1", 1, 2, 3));

        Assert.False(store.Remembers("a"));
        Assert.Equal(5, store.ResolveSum("entity-1", "offset"));

        Apply(store, first);

        Assert.Equal(2, store.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(3, store.RawContribution("entity-1", "peer-B", "offset"));
        Assert.Equal(5, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(2, store.RecordCount);
    }

    [Fact]
    public void A_one_slot_window_keeps_a_lower_lamport_on_another_field_in_either_order()
    {
        var label = new AetherOperation("a", "entity-1", "peer-A", "peer-A/s1", 1, 100, new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("cube"),
        });
        var visible = new AetherOperation("b", "entity-1", "peer-B", "peer-B/s1", 1, 101, new Dictionary<string, FieldValue>
        {
            ["visible"] = FieldValue.Label("true"),
        });
        var offset = FixedOp("c", "peer-C", "peer-C/s1", 1, 50, 7);

        foreach (var order in new[] { new[] { label, visible, offset }, new[] { offset, label, visible } })
        {
            var store = NewStore(dedupCapacity: 1);
            store.DefineField("visible", "lww");
            foreach (var op in order)
                Apply(store, op);

            Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
            Assert.Equal("true", store.ResolveLww("entity-1", "visible")?.Text);
            Assert.Equal(7, store.ResolveSum("entity-1", "offset"));
            Assert.Equal(1, store.DedupCount);
        }
    }

    [Fact]
    public void An_older_remote_lamport_still_advances_a_higher_local_clock()
    {
        var store = NewStore();
        Apply(store, LabelOp("high", "peer-A", "peer-A/s1", 1, 100, "now"));
        Assert.Equal(101, store.Clock);

        Apply(store, LabelOp("older", "peer-B", "peer-B/s1", 1, 40, "earlier"));

        Assert.Equal(102, store.Clock);
        Assert.Equal("now", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Frozen_plan_matches_the_direct_reducer_and_the_expected_view()
    {
        var ops = new[]
        {
            LabelOp("a-label", "peer-A", "peer-A/s1", 1, 10, "cube"),
            FixedOp("a-offset", "peer-A", "peer-A/s1", 2, 11, 2),
            FixedOp("b-offset", "peer-B", "peer-B/s1", 1, 12, 3),
        };

        var direct = NewStore();
        foreach (var op in ops)
            Apply(direct, op);

        var viaPlan = NewStore();
        var plan = AetherFrame.Compile();
        var scratch = new AetherScratch
        {
            Reducer = viaPlan,
            Inbox = ops.ToList(),
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
        };
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        plan.Tick(ref scratch, ref runtime);

        Assert.Equal("cube", direct.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(5, direct.ResolveSum("entity-1", "offset"));
        Assert.Equal(direct.ResolveLww("entity-1", "label")?.Text, scratch.ResolvedLabel);
        Assert.Equal(direct.ResolveSum("entity-1", "offset"), scratch.ResolvedSum);
        Assert.Equal(direct.RecordCount, viaPlan.RecordCount);
        Assert.Equal(3, viaPlan.RecordCount);
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
}
