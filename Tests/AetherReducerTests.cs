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
    public void Equal_lamport_keeps_the_greater_utf8_actor_id()
    {
        var replacement = "\uFFFD";
        var emoji = "\U0001F600";
        Assert.True(string.CompareOrdinal(emoji, replacement) < 0);

        foreach (var first in new[] { replacement, emoji })
        {
            var store = NewStore();
            Apply(store, ActorLabel(first == emoji ? "emoji-first" : "replacement-first", first, first == emoji ? "emoji" : "replacement"));
            var second = first == emoji ? replacement : emoji;
            Apply(store, ActorLabel(second == emoji ? "emoji-second" : "replacement-second", second, second == emoji ? "emoji" : "replacement"));

            Assert.Equal("emoji", store.ResolveLww("entity-1", "label")?.Text);
        }
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
    public void A_field_schema_cannot_be_redefined_or_inverted()
    {
        var store = new AetherReducer();
        store.DefineField("offset", "sumContributions", 0, 10);

        Assert.Throws<AetherProtocolException>(() => store.DefineField("offset", "lww"));
        Assert.Throws<AetherProtocolException>(() => store.DefineField("wide", "sumContributions", 5, 4));
        Assert.Equal(0, store.RecordCount);
    }

    [Fact]
    public void Distinct_payloads_keep_distinct_digests()
    {
        var left = new AetherOperation("same", "e", "w", "a", 1, 1, new Dictionary<string, FieldValue>());
        var right = new AetherOperation("same", "e|w", "a", "1", 1, 1, new Dictionary<string, FieldValue>());
        Assert.NotEqual(left.Digest, right.Digest);
        Assert.Equal(64, left.Digest.Length);

        var zero = new AetherOperation("zero", "e", "w", "a", 1, 1, new Dictionary<string, FieldValue>
        {
            ["offset"] = FieldValue.FixedPoint(0),
        });
        var emptyLabel = new AetherOperation("empty", "e", "w", "a", 1, 1, new Dictionary<string, FieldValue>
        {
            ["offset"] = FieldValue.Label(""),
        });
        Assert.NotEqual(zero.Digest, emptyLabel.Digest);

        var bulky = LabelOp("n", "peer-A", "peer-A/s1", 1, 1, new string('x', 4000));
        Assert.Equal(64, bulky.Digest.Length);

        var store = NewStore();
        Apply(store, left);
        var clock = store.Clock;
        Assert.Throws<AetherProtocolException>(() => Apply(store, right));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(0, store.RecordCount);
    }

    [Fact]
    public void An_operation_payload_stays_fixed_after_construction()
    {
        var changes = new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("cube"),
        };
        var op = new AetherOperation("id", "entity-1", "peer-A", "peer-A/s1", 1, 1, changes);
        var digest = op.Digest;
        changes["label"] = FieldValue.Label("box");

        Assert.Equal("cube", op.Changes["label"].Text);
        Assert.Equal(digest, op.Digest);
        Assert.False(op.Changes is Dictionary<string, FieldValue>);
    }

    [Fact]
    public void A_sum_field_rejects_a_label_without_changing_the_contribution()
    {
        var store = NewStore();
        Apply(store, FixedOp("a", "peer-A", "peer-A/s1", 1, 1, 4));
        var clock = store.Clock;
        var bad = new AetherOperation("b", "entity-1", "peer-A", "peer-A/s1", 2, 8, new Dictionary<string, FieldValue>
        {
            ["offset"] = FieldValue.Label("hibás"),
        });

        Assert.Throws<AetherProtocolException>(() => Apply(store, bad));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(4, store.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(4, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(1, store.RecordCount);
        Assert.False(store.Remembers("b"));

        var image = new AetherImage(
            80,
            [
                new StoredField(
                    new RecordKey("entity-1", "peer-A", "offset"),
                    FieldValue.Label("hibás"),
                    new FieldVersion(30, "peer-A/s1"),
                    "peer-A",
                    "bad"),
            ],
            []);
        Assert.Throws<AetherProtocolException>(() => store.MergeImage(image));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(4, store.RawContribution("entity-1", "peer-A", "offset"));
    }

    [Fact]
    public void A_rejected_snapshot_leaves_the_reducer_unchanged()
    {
        var store = NewStore();
        Apply(store, LabelOp("live", "peer-A", "peer-A/s1", 1, 10, "cube"));
        Apply(store, FixedOp("off", "peer-B", "peer-B/s1", 1, 4, 3));
        var clock = store.Clock;
        var dedup = store.DedupCount;

        var image = new AetherImage(
            999,
            [
                new StoredField(
                    new RecordKey("entity-1", "peer-B", "offset"),
                    FieldValue.FixedPoint(9),
                    new FieldVersion(20, "peer-B/s1"),
                    "peer-B",
                    "newer-off"),
                new StoredField(
                    new RecordKey("entity-1", "peer-A", "label"),
                    FieldValue.Label("box"),
                    new FieldVersion(10, "peer-A/s1"),
                    "peer-A",
                    "clash"),
            ],
            [new SeenOperation("intruder", "abc", 20)]);

        Assert.Throws<AetherProtocolException>(() => store.MergeImage(image));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(dedup, store.DedupCount);
        Assert.False(store.Remembers("intruder"));
        Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(3, store.RawContribution("entity-1", "peer-B", "offset"));

        var within = NewStore();
        var colliding = new AetherImage(
            5,
            [
                new StoredField(
                    new RecordKey("entity-1", "peer-A", "label"),
                    FieldValue.Label("one"),
                    new FieldVersion(1, "peer-A/s1"),
                    "peer-A",
                    "row-1"),
                new StoredField(
                    new RecordKey("entity-1", "peer-A", "label"),
                    FieldValue.Label("two"),
                    new FieldVersion(1, "peer-A/s1"),
                    "peer-A",
                    "row-2"),
            ],
            []);
        Assert.Throws<AetherProtocolException>(() => within.MergeImage(colliding));
        Assert.Equal(0, within.Clock);
        Assert.Equal(0, within.DedupCount);
        Assert.Equal(0, within.RecordCount);

        var digests = NewStore();
        var clashingDigests = new AetherImage(
            5,
            [],
            [
                new SeenOperation("x", "one", 1),
                new SeenOperation("x", "two", 1),
            ]);
        Assert.Throws<AetherProtocolException>(() => digests.MergeImage(clashingDigests));
        Assert.Equal(0, digests.Clock);
        Assert.Equal(0, digests.DedupCount);
    }

    [Fact]
    public void A_lower_snapshot_row_cannot_conflict_under_a_newer_one()
    {
        var store = NewStore();
        Apply(store, LabelOp("live", "peer-A", "peer-A/s1", 1, 10, "cube"));
        var clock = store.Clock;
        var dedup = store.DedupCount;

        var image = new AetherImage(
            999,
            [
                LabelRow(10, "box", "old-clash"),
                LabelRow(20, "fresh", "newer"),
            ],
            [new SeenOperation("intruder", "abc", 20)]);

        Assert.Throws<AetherProtocolException>(() => store.MergeImage(image));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(dedup, store.DedupCount);
        Assert.False(store.Remembers("intruder"));
        Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void A_lower_version_collision_is_rejected_in_either_row_order()
    {
        foreach (var rows in new[]
        {
            new[] { LabelRow(20, "new", "high"), LabelRow(10, "x", "low-x"), LabelRow(10, "y", "low-y") },
            new[] { LabelRow(10, "x", "low-x"), LabelRow(10, "y", "low-y"), LabelRow(20, "new", "high") },
        })
        {
            var store = NewStore();
            Assert.Throws<AetherProtocolException>(() => store.MergeImage(new AetherImage(5, rows, [])));
            Assert.Equal(0, store.Clock);
            Assert.Equal(0, store.RecordCount);
            Assert.Equal(0, store.DedupCount);
        }
    }

    [Fact]
    public void Agreeing_lower_rows_fold_to_the_newer_value()
    {
        var store = NewStore();
        store.MergeImage(new AetherImage(
            5,
            [
                LabelRow(10, "x", "low-a"),
                LabelRow(10, "x", "low-b"),
                LabelRow(20, "new", "high"),
            ],
            [new SeenOperation("s", "d", 20)]));

        Assert.Equal("new", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(5, store.Clock);
        Assert.Equal(1, store.RecordCount);
        Assert.True(store.Remembers("s"));
    }

    [Fact]
    public void A_lamport_that_cannot_advance_is_rejected_before_any_change()
    {
        var store = NewStore();
        Apply(store, LabelOp("live", "peer-A", "peer-A/s1", 1, 1, "cube"));
        var clock = store.Clock;
        var dedup = store.DedupCount;

        Assert.Throws<AetherProtocolException>(() =>
            Apply(store, LabelOp("max", "peer-B", "peer-B/s1", 1, long.MaxValue, "later")));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(dedup, store.DedupCount);
        Assert.Equal(1, store.RecordCount);
        Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
        Assert.False(store.Remembers("max"));

        store.MergeImage(new AetherImage(long.MaxValue, [], []));
        Assert.Equal(long.MaxValue, store.Clock);
        Assert.Throws<AetherProtocolException>(() => store.StampLocal(
            "entity-1",
            "peer-A",
            "peer-A/s1",
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("next") }));
        Assert.Throws<AetherProtocolException>(() =>
            Apply(store, LabelOp("after", "peer-B", "peer-B/s1", 1, 1, "nope")));
        Assert.Equal(long.MaxValue, store.Clock);
        Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
        Assert.False(store.Remembers("after"));
    }

    [Fact]
    public void Delete_and_update_leave_the_entity_deleted_in_either_order()
    {
        foreach (var deleteFirst in new[] { true, false })
        {
            var store = NewStore();
            var update = LabelOp("update", "peer-A", "peer-A/s1", 1, 10, "cube");
            var delete = Life(AetherEffect.DeleteEntity, "delete", 5);
            if (deleteFirst)
            {
                Apply(store, delete);
                Apply(store, update);
            }
            else
            {
                Apply(store, update);
                Apply(store, delete);
            }

            Apply(store, Life(AetherEffect.RestoreShared, "restore", 30, "peer-B/s1"));
            Assert.True(store.IsDeleted("entity-1"));
            Assert.Equal("cube", store.ResolveLww("entity-1", "label")?.Text);
        }
    }

    [Fact]
    public void A_snapshot_from_before_delete_does_not_revive()
    {
        var origin = NewStore();
        Apply(origin, LabelOp("old", "peer-A", "peer-A/s1", 1, 10, "cube"));
        var before = origin.Capture();
        Apply(origin, Life(AetherEffect.DeleteEntity, "delete", 20));
        origin.MergeImage(before);

        Assert.True(origin.IsDeleted("entity-1"));
        Assert.Equal("cube", origin.ResolveLww("entity-1", "label")?.Text);

        var live = NewStore();
        Apply(live, LabelOp("new", "peer-A", "peer-A/s1", 2, 30, "box"));
        live.MergeImage(origin.Capture());
        Assert.True(live.IsDeleted("entity-1"));
        Assert.Equal("box", live.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Hide_and_restore_follow_the_greater_version_in_either_order()
    {
        foreach (var hideFirst in new[] { true, false })
        {
            var hidden = NewStore();
            ApplyPair(
                hidden,
                hideFirst,
                Life(AetherEffect.HideShared, "hide", 20),
                Life(AetherEffect.RestoreShared, "restore", 10, "peer-B/s1"));
            Assert.False(hidden.IsSharedVisible("entity-1"));

            var shown = NewStore();
            ApplyPair(
                shown,
                hideFirst,
                Life(AetherEffect.HideShared, "hide", 10),
                Life(AetherEffect.RestoreShared, "restore", 20, "peer-B/s1"));
            Assert.True(shown.IsSharedVisible("entity-1"));
        }
    }

    [Fact]
    public void Hide_keeps_the_raw_contribution()
    {
        var store = NewStore();
        Apply(store, FixedOp("a", "peer-A", "peer-A/s1", 1, 1, 4));
        Apply(store, Life(AetherEffect.HideShared, "hide", 2));

        Assert.False(store.IsSharedVisible("entity-1"));
        Assert.Equal(4, store.RawContribution("entity-1", "peer-A", "offset"));
        Assert.Equal(4, store.ResolveSum("entity-1", "offset"));

        Apply(store, Life(AetherEffect.RestoreShared, "restore", 3));
        Assert.True(store.IsSharedVisible("entity-1"));
        Assert.Equal(4, store.RawContribution("entity-1", "peer-A", "offset"));
    }

    [Fact]
    public void A_snapshot_from_before_withdraw_does_not_restore_membership()
    {
        var origin = NewStore();
        Apply(origin, Life(AetherEffect.Publish, "pub", 10, publicationId: "catalog"));
        var before = origin.Capture();
        Apply(origin, Life(AetherEffect.WithdrawPublication, "wd", 20, publicationId: "catalog"));
        origin.MergeImage(before);
        Assert.False(origin.IsMember("entity-1", "catalog"));

        var live = NewStore();
        Apply(live, Life(AetherEffect.Publish, "pub", 10, publicationId: "catalog"));
        live.MergeImage(origin.Capture());
        Assert.False(live.IsMember("entity-1", "catalog"));
    }

    [Fact]
    public void Republish_needs_a_higher_membership_version()
    {
        var store = NewStore();
        Apply(store, Life(AetherEffect.Publish, "pub", 10, publicationId: "catalog"));
        Apply(store, Life(AetherEffect.WithdrawPublication, "wd", 20, publicationId: "catalog"));
        Apply(store, Life(AetherEffect.Publish, "older", 15, publicationId: "catalog"));
        Assert.False(store.IsMember("entity-1", "catalog"));

        Apply(store, Life(AetherEffect.Publish, "again", 30, publicationId: "catalog"));
        Assert.True(store.IsMember("entity-1", "catalog"));
    }

    [Fact]
    public void Withdraw_removes_one_publication_and_leaves_the_other()
    {
        var store = NewStore();
        Apply(store, Life(AetherEffect.Publish, "cat", 10, publicationId: "catalog"));
        Apply(store, Life(AetherEffect.Publish, "notes", 11, publicationId: "notes"));
        Apply(store, FixedOp("a", "peer-A", "peer-A/s1", 1, 1, 4));
        Apply(store, Life(AetherEffect.WithdrawPublication, "wd", 12, publicationId: "notes"));

        Assert.True(store.IsMember("entity-1", "catalog"));
        Assert.False(store.IsMember("entity-1", "notes"));
        Assert.False(store.IsDeleted("entity-1"));
        Assert.Equal(4, store.RawContribution("entity-1", "peer-A", "offset"));
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
            Life(AetherEffect.Publish, "pub", 13, publicationId: "catalog"),
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
            PublicationId = "catalog",
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

    [Fact]
    public void Frozen_plan_hides_a_deleted_entity_and_keeps_the_raw_field()
    {
        var delete = Life(AetherEffect.DeleteEntity, "delete", 20);
        var ops = new[]
        {
            LabelOp("a-label", "peer-A", "peer-A/s1", 1, 10, "cube"),
            Life(AetherEffect.Publish, "pub", 11, publicationId: "catalog"),
            delete,
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
            PublicationId = "catalog",
        };
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        plan.Tick(ref scratch, ref runtime);

        Assert.True(direct.IsDeleted("entity-1"));
        Assert.Equal("cube", direct.ResolveLww("entity-1", "label")?.Text);
        Assert.True(scratch.ResolvedDeleted);
        Assert.True(scratch.ResolvedSharedVisible);
        Assert.True(scratch.ResolvedMember);
        Assert.Null(scratch.ResolvedLabel);
        Assert.Equal(0, scratch.ResolvedSum);
        Assert.Equal(direct.IsDeleted("entity-1"), viaPlan.IsDeleted("entity-1"));
    }

    [Fact]
    public void Frozen_plan_drops_the_shared_view_when_the_last_publication_is_withdrawn()
    {
        var ops = new[]
        {
            LabelOp("write", "peer-A", "peer-A/s1", 1, 10, "secret"),
            Life(AetherEffect.Publish, "pub", 11, publicationId: "catalog"),
            Life(AetherEffect.WithdrawPublication, "wd", 12, publicationId: "catalog"),
        };
        var scratch = TickPlan(ops, "catalog");

        Assert.False(scratch.Reducer.IsMember("entity-1", "catalog"));
        Assert.False(scratch.ResolvedDeleted);
        Assert.True(scratch.ResolvedSharedVisible);
        Assert.False(scratch.ResolvedMember);
        Assert.Null(scratch.ResolvedLabel);
        Assert.Equal(0, scratch.ResolvedSum);
        Assert.Equal("secret", scratch.Reducer.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Frozen_plan_keeps_the_shared_view_for_a_publication_that_stays_active()
    {
        var ops = new[]
        {
            LabelOp("write", "peer-A", "peer-A/s1", 1, 10, "secret"),
            Life(AetherEffect.Publish, "cat", 11, publicationId: "catalog"),
            Life(AetherEffect.Publish, "notes", 12, publicationId: "notes"),
            Life(AetherEffect.WithdrawPublication, "wd", 13, publicationId: "notes"),
        };
        var scratch = TickPlan(ops, "catalog");

        Assert.False(scratch.ResolvedDeleted);
        Assert.True(scratch.ResolvedSharedVisible);
        Assert.True(scratch.ResolvedMember);
        Assert.True(scratch.Reducer.IsMember("entity-1", "catalog"));
        Assert.False(scratch.Reducer.IsMember("entity-1", "notes"));
        Assert.Equal("secret", scratch.ResolvedLabel);
        Assert.Equal("secret", scratch.Reducer.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Frozen_plan_without_a_publication_does_not_emit_a_shared_view()
    {
        var ops = new[]
        {
            LabelOp("write", "peer-A", "peer-A/s1", 1, 10, "secret"),
            Life(AetherEffect.Publish, "pub", 11, publicationId: "catalog"),
        };

        foreach (var publicationId in new string?[] { null, "" })
        {
            var scratch = TickPlan(ops, publicationId);
            Assert.True(scratch.Reducer.IsMember("entity-1", "catalog"));
            Assert.False(scratch.ResolvedDeleted);
            Assert.True(scratch.ResolvedSharedVisible);
            Assert.False(scratch.ResolvedMember);
            Assert.Null(scratch.ResolvedLabel);
            Assert.Equal(0, scratch.ResolvedSum);
            Assert.Equal("secret", scratch.Reducer.ResolveLww("entity-1", "label")?.Text);
        }
    }

    private static AetherScratch TickPlan(IReadOnlyList<AetherOperation> ops, string? publicationId)
    {
        var plan = AetherFrame.Compile();
        var scratch = new AetherScratch
        {
            Reducer = NewStore(),
            Inbox = ops.ToList(),
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = publicationId,
        };
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        plan.Tick(ref scratch, ref runtime);
        return scratch;
    }

    private static AetherReducer NewStore(long sumMax = long.MaxValue, int dedupCapacity = AetherReducer.DefaultDedupCapacity)
    {
        var store = new AetherReducer(dedupCapacity);
        store.DefineField("label", "lww");
        store.DefineField("offset", "sumContributions", long.MinValue, sumMax);
        return store;
    }

    private static void Apply(AetherReducer store, AetherOperation op) => store.Apply(op, observeClock: true);

    private static void ApplyPair(AetherReducer store, bool firstWins, AetherOperation first, AetherOperation second)
    {
        if (firstWins)
        {
            Apply(store, first);
            Apply(store, second);
            return;
        }

        Apply(store, second);
        Apply(store, first);
    }

    private static AetherOperation Life(
        AetherEffect effect,
        string id,
        long lamport,
        string actorId = "peer-A/s1",
        string? publicationId = null) =>
        new(id, "entity-1", "peer-A", actorId, 1, lamport, new Dictionary<string, FieldValue>(), effect, publicationId);

    private static StoredField LabelRow(long lamport, string label, string operationId) =>
        new(
            new RecordKey("entity-1", "peer-A", "label"),
            FieldValue.Label(label),
            new FieldVersion(lamport, "peer-A/s1"),
            "peer-A",
            operationId);

    private static AetherOperation ActorLabel(string id, string actorId, string label) =>
        new(id, "entity-1", "peer-A", actorId, 1, 10, new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label(label),
        });

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
