using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;

namespace Ape.Core.Tests;

/// <summary>
/// <see cref="FrameContext.PolicyVersion"/> flows through <see cref="FrameProcessingHost.RunFrame(long, string?)"/>;
/// <see cref="IngressBuffer{T}"/> ordering drives deterministic batch contents for processors.
/// </summary>
public class FrameContextAndIngressDeterminismTests
{
    private const string Epoch = "ep";
    private sealed class CollectingSink : ISceneCommitSink
    {
        public List<ISceneCommitRequest> Commits { get; } = new();

        public void Enqueue(ISceneCommitRequest request) => Commits.Add(request);
    }

    /// <summary>Embeds policy in scene commit (Scale) and echoes policy tag in outputs.</summary>
    private sealed class PolicyAwareProcessor : DeterministicFrameLayerBase<byte, string>
    {
        public const string NodeKey = "PolicyDeterminismNode";

        protected override ProcessingResult<string> OnProcessFrame(
            in FrameContext context,
            IReadOnlyList<IngressEnvelope<byte>> batch)
        {
            var fp = PolicyFingerprint(context.PolicyVersion);
            DeterministicSceneWrites.RecordNodeProperty(NodeKey, nameof(Node.Scale), new Vector3(context.FrameId, fp, 0f));
            var tag = $"{context.FrameId}|{context.PolicyVersion ?? ""}";
            return new ProcessingResult<string>(new[] { tag }, null);
        }

        /// <summary>Stable fingerprint without <see cref="string.GetHashCode"/> (runtime-dependent).</summary>
        private static float PolicyFingerprint(string? policy)
        {
            if (string.IsNullOrEmpty(policy))
                return 0f;
            float s = 0f;
            foreach (var c in policy)
                s += c;
            return s;
        }
    }

    private sealed class SumIngressProcessor : DeterministicFrameLayerBase<int, int>
    {
        protected override ProcessingResult<int> OnProcessFrame(
            in FrameContext context,
            IReadOnlyList<IngressEnvelope<int>> batch)
        {
            var sum = 0;
            foreach (var e in batch)
                sum += e.Payload;
            return new ProcessingResult<int>(new[] { sum }, null);
        }
    }

    [Fact]
    public void Same_frame_and_policy_produce_identical_outputs_and_commits_twice()
    {
        var a = RunPolicyAware(100L, "sim-v2");
        var b = RunPolicyAware(100L, "sim-v2");
        Assert.Equal(a.Outputs, b.Outputs);
        Assert.Equal(a.Commits.Count, b.Commits.Count);
        for (var i = 0; i < a.Commits.Count; i++)
            Assert.Equal(a.Commits[i].ToString(), b.Commits[i].ToString());
    }

    [Fact]
    public void Same_frame_different_policy_versions_produce_different_outputs()
    {
        var left = RunPolicyAware(5L, "policy-a");
        var right = RunPolicyAware(5L, "policy-b");
        Assert.NotEqual(left.Outputs[0], right.Outputs[0]);
        Assert.NotEqual(left.Commits[0].ToString(), right.Commits[0].ToString());
    }

    [Fact]
    public void Ingress_drain_order_determines_batch_sum_even_when_enqueue_order_differs()
    {
        var first = RunSumWithIngress(enqueueOrder: new[] { (10, 3L), (20, 1L), (30, 2L) });
        var second = RunSumWithIngress(enqueueOrder: new[] { (20, 1L), (30, 2L), (10, 3L) });
        Assert.Equal(first, second);
        Assert.Equal(60, first);
    }

    [Fact]
    public void Same_ingress_pattern_twice_yields_same_sum_output()
    {
        var a = RunSumWithIngress(new[] { (1, 1L), (2, 2L) });
        var b = RunSumWithIngress(new[] { (1, 1L), (2, 2L) });
        Assert.Equal(a, b);
        Assert.Equal(3, a);
    }

    [Fact]
    public void SourceSequence_enqueue_without_explicit_sequence_throws()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        Assert.Throws<ArgumentException>(() => buffer.Enqueue(1, sourceEpoch: Epoch));
    }

    [Fact]
    public void SourceSequence_enqueue_without_epoch_throws()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        Assert.Throws<ArgumentException>(() => buffer.Enqueue(1, explicitSequence: 1));
    }

    [Fact]
    public void ObservationTime_drain_sorts_by_time_then_sequence()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.ObservationTime);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 2, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        buffer.Enqueue(20, explicitSequence: 2, sourceObservationTime: t0, sourceEpoch: Epoch);
        buffer.Enqueue(10, explicitSequence: 1, sourceObservationTime: t1, sourceEpoch: Epoch);
        var drained = buffer.DrainOrdered();
        Assert.Equal(new[] { 10, 20 }, drained.Select(e => e.Payload).ToArray());
    }

    [Fact]
    public void ObservationTime_strict_sequence_preserves_maximum_watermark()
    {
        var buffer = new IngressBuffer<int>(
            IngressOrdering.ObservationTime,
            IngressSequencePolicy.StrictlyIncreasing);

        var earlier = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddSeconds(1);

        buffer.Enqueue(
            200,
            explicitSequence: 200,
            sourceObservationTime: earlier,
            sourceEpoch: Epoch);

        buffer.Enqueue(
            100,
            explicitSequence: 100,
            sourceObservationTime: later,
            sourceEpoch: Epoch);

        buffer.DrainOrdered();

        Assert.Throws<InvalidOperationException>(() =>
            buffer.Enqueue(
                150,
                explicitSequence: 150,
                sourceObservationTime: later.AddSeconds(1),
                sourceEpoch: Epoch));
    }

    [Fact]
    public void ObservationTime_enqueue_without_timestamp_throws()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.ObservationTime);
        Assert.Throws<ArgumentException>(() => buffer.Enqueue(1, explicitSequence: 1, sourceEpoch: Epoch));
    }

    [Fact]
    public void ObservationTime_enqueue_without_sequence_throws()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.ObservationTime);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Throws<ArgumentException>(() => buffer.Enqueue(1, sourceObservationTime: t, sourceEpoch: Epoch));
    }

    [Fact]
    public void SourceSequence_duplicate_sequence_throws()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        buffer.Enqueue(1, explicitSequence: 7, sourceEpoch: Epoch);
        Assert.Throws<InvalidOperationException>(() => buffer.Enqueue(2, explicitSequence: 7, sourceEpoch: Epoch));
    }

    [Fact]
    public void CaptureOrder_rejects_explicit_sequence()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.CaptureOrder);
        Assert.Throws<ArgumentException>(() => buffer.Enqueue(1, explicitSequence: 1));
    }

    [Fact]
    public void SourceSequence_rejects_same_sequence_after_drain_until_reset()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        buffer.Enqueue(1, explicitSequence: 42, sourceEpoch: Epoch);
        Assert.Single(buffer.DrainOrdered());
        Assert.Throws<InvalidOperationException>(() => buffer.Enqueue(2, explicitSequence: 42, sourceEpoch: Epoch));
        buffer.ResetSession("reconnect-1");
        buffer.Enqueue(3, explicitSequence: 42, sourceEpoch: "reconnect-1");
        Assert.Equal(3, buffer.DrainOrdered()[0].Payload);
    }

    [Fact]
    public void UniqueButOutOfOrder_allows_lower_sequence_if_unseen()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence, IngressSequencePolicy.UniqueButOutOfOrder);
        buffer.Enqueue(1, explicitSequence: 10, sourceEpoch: Epoch);
        buffer.DrainOrdered();
        buffer.Enqueue(2, explicitSequence: 8, sourceEpoch: Epoch);
        Assert.Equal(8, buffer.DrainOrdered()[0].Sequence);
        Assert.Throws<InvalidOperationException>(() => buffer.Enqueue(3, explicitSequence: 10, sourceEpoch: Epoch));
    }

    [Fact]
    public void UniqueButOutOfOrder_evicts_sequences_outside_reorder_window()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence, IngressSequencePolicy.UniqueButOutOfOrder, reorderWindow: 2);
        buffer.Enqueue(1, explicitSequence: 1, sourceEpoch: Epoch);
        buffer.DrainOrdered();
        buffer.Enqueue(2, explicitSequence: 2, sourceEpoch: Epoch);
        buffer.DrainOrdered();
        buffer.Enqueue(3, explicitSequence: 3, sourceEpoch: Epoch);
        buffer.DrainOrdered();
        buffer.Enqueue(4, explicitSequence: 1, sourceEpoch: Epoch);
        Assert.Equal(1, buffer.DrainOrdered()[0].Sequence);
    }

    [Fact]
    public void WrappingCounter_accepts_low_sequence_after_high()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence, IngressSequencePolicy.WrappingCounter);
        buffer.Enqueue(1, explicitSequence: 65000, sourceEpoch: Epoch);
        buffer.DrainOrdered();
        buffer.Enqueue(2, explicitSequence: 10, sourceEpoch: Epoch);
        Assert.Equal(10, buffer.DrainOrdered()[0].Sequence);
    }

    [Fact]
    public void WrappingCounter_rejects_stale_high_sequence()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence, IngressSequencePolicy.WrappingCounter);
        buffer.Enqueue(1, explicitSequence: 100, sourceEpoch: Epoch);
        buffer.DrainOrdered();
        Assert.Throws<InvalidOperationException>(() => buffer.Enqueue(2, explicitSequence: 65000, sourceEpoch: Epoch));
    }

    [Fact]
    public void WrappingCounter_drains_wrap_batch_in_extended_order()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence, IngressSequencePolicy.WrappingCounter);
        buffer.Enqueue(1, explicitSequence: 65534, sourceEpoch: Epoch);
        buffer.Enqueue(2, explicitSequence: 65535, sourceEpoch: Epoch);
        buffer.Enqueue(3, explicitSequence: 0, sourceEpoch: Epoch);
        buffer.Enqueue(4, explicitSequence: 1, sourceEpoch: Epoch);
        var drained = buffer.DrainOrdered();
        Assert.Equal(new[] { 1, 2, 3, 4 }, drained.Select(e => e.Payload).ToArray());
        Assert.Equal(new[] { 65534L, 65535L, 0L, 1L }, drained.Select(e => e.Sequence).ToArray());
    }

    [Fact]
    public void WrappingCounter_survives_more_than_one_full_cycle()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence, IngressSequencePolicy.WrappingCounter);
        for (var i = 0; i < 70000; i++)
        {
            var seq = i % IngressWrap.Modulus;
            buffer.Enqueue(i, explicitSequence: seq, sourceEpoch: Epoch);
            Assert.Equal(seq, buffer.DrainOrdered()[0].Sequence);
        }
    }

    [Fact]
    public void ResetSession_drops_pending_and_named_epoch_cannot_be_reused()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        buffer.ResetSession("radar-01/epoch-a");
        buffer.Enqueue(1, explicitSequence: 1, sourceEpoch: "radar-01/epoch-a");
        buffer.ResetSession("radar-01/epoch-b");
        Assert.Empty(buffer.DrainOrdered());
        buffer.Enqueue(2, explicitSequence: 1, sourceEpoch: "radar-01/epoch-b");
        Assert.Equal(2, buffer.DrainOrdered()[0].Payload);
        Assert.Throws<InvalidOperationException>(() => buffer.ResetSession("radar-01/epoch-a"));
    }

    [Fact]
    public void Stale_epoch_packets_are_dropped_reconnect_is_not_wrap()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        buffer.ResetSession("conn-1");
        buffer.Enqueue(1, explicitSequence: 1, sourceEpoch: "conn-1");
        buffer.DrainOrdered();
        buffer.ResetSession("conn-2");
        buffer.Enqueue(99, explicitSequence: 1, sourceEpoch: "conn-1");
        Assert.Equal(1, buffer.DroppedStaleEpochCount);
        Assert.Empty(buffer.DrainOrdered());
        buffer.Enqueue(2, explicitSequence: 1, sourceEpoch: "conn-2");
        Assert.Equal(2, buffer.DrainOrdered()[0].Payload);
    }

    [Fact]
    public void FromFrame_uses_epoch_and_duration_from_journal_header()
    {
        var header = new SampleFrameJournalHeader(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(20));
        var t = LogicalFrameTime.FromFrame(3, header.LogicalEpoch, header.FrameDuration);
        Assert.Equal(header.LogicalEpoch + TimeSpan.FromMilliseconds(60), t);
        Assert.NotEqual(LogicalFrameTime.FromFrameId(3), t);
    }

    [Fact]
    public void Same_frame_id_yields_identical_logical_time()
    {
        Assert.Equal(LogicalFrameTime.FromFrameId(42), LogicalFrameTime.FromFrameId(42));
        Assert.NotEqual(LogicalFrameTime.FromFrameId(42), LogicalFrameTime.FromFrameId(43));
    }

    private static (IReadOnlyList<string> Outputs, List<ISceneCommitRequest> Commits) RunPolicyAware(long frameId, string? policyVersion)
    {
        var sink = new CollectingSink();
        var processor = new PolicyAwareProcessor();
        var host = new FrameProcessingHost<byte, string>(processor);
        using (DeterministicWriteScope.Begin(sink, frameId))
        {
            var result = host.RunFrame(frameId, policyVersion);
            return (result.Outputs, sink.Commits);
        }
    }

    private static int RunSumWithIngress((int payload, long sequence)[] enqueueOrder)
    {
        var processor = new SumIngressProcessor();
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        foreach (var (payload, seq) in enqueueOrder)
            buffer.Enqueue(payload, explicitSequence: seq, sourceEpoch: Epoch);
        var host = new FrameProcessingHost<int, int>(processor, buffer);
        var result = host.RunFrame(1L);
        return result.Outputs[0];
    }
}
