using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;

namespace Ape.Core.Tests;

/// <summary>
/// Determinism: same <see cref="FrameContext.FrameId"/> and same processor logic must yield the same
/// <see cref="ISceneCommitRequest"/> sequence when routed through <see cref="DeterministicWriteScope"/> +
/// <see cref="DeterministicSceneWrites"/> (the path used by host-frame simulation).
/// </summary>
public class DeterminismTests
{
    /// <summary>Regression guard: SHA256 of culture-invariant commit snapshot for <see cref="PureFrameProcessor"/> at frame 42.</summary>
    private const string GoldenFrame42CommitBatchSha256Hex =
        "1FA16355D0611D6A307FCE6A8243AB285F8F803E92A75A74D7AD38359EA21B6E";

    private sealed class CollectingSink : ISceneCommitSink
    {
        public List<ISceneCommitRequest> Commits { get; } = new();

        public void Enqueue(ISceneCommitRequest request) => Commits.Add(request);
    }

    /// <summary>Stateless: commit payload is a pure function of <see cref="FrameContext.FrameId"/> (no static/instance sim state).</summary>
    private sealed class PureFrameProcessor : DeterministicFrameLayerBase<byte, int>
    {
        public const string NodeKey = "DeterminismTestNode";

        protected override ProcessingResult<int> OnProcessFrame(
            in FrameContext context,
            IReadOnlyList<IngressEnvelope<byte>> batch)
        {
            float t = context.FrameId * 0.05f;
            var pos = new Vector3(MathF.Sin(t) * 5f, context.FrameId * 0.01f, MathF.Cos(t) * 5f);
            DeterministicSceneWrites.RecordNodeProperty(NodeKey, nameof(Node.Position), pos);
            return new ProcessingResult<int>(new[] { (int)context.FrameId }, null);
        }
    }

    [Fact]
    public void Same_frame_id_produces_identical_commit_sequence_twice()
    {
        var first = RunFrameCollectCommits(42L);
        var second = RunFrameCollectCommits(42L);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Equal(first[i].ToString(), second[i].ToString());
    }

    [Fact]
    public void Pure_processor_each_frame_id_in_range_produces_identical_commit_pair()
    {
        for (var frameId = 0L; frameId < 500L; frameId++)
        {
            var first = RunFrameCollectCommits(frameId);
            var second = RunFrameCollectCommits(frameId);
            Assert.Equal(first.Count, second.Count);
            for (var i = 0; i < first.Count; i++)
                Assert.Equal(first[i].ToString(), second[i].ToString());
        }
    }

    [Fact]
    public void Same_frame_id_produces_identical_processing_outputs_twice()
    {
        var processor = new PureFrameProcessor();
        var host = new FrameProcessingHost<byte, int>(processor);
        var sink = new CollectingSink();
        int[] first;
        using (DeterministicWriteScope.Begin(sink, 77L))
            first = host.RunFrame(77L).Outputs.ToArray();
        sink = new CollectingSink();
        int[] second;
        using (DeterministicWriteScope.Begin(sink, 77L))
            second = host.RunFrame(77L).Outputs.ToArray();
        Assert.Equal(first, second);
        Assert.Equal(77, first[0]);
    }

    [Fact]
    public void Golden_frame_42_commit_batch_matches_invariant_sha256_snapshot()
    {
        var commits = RunFrameCollectCommits(42L);
        var invariant = string.Join('\n', commits.Select(InvariantCommitSnapshot));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invariant)));
        Assert.Equal(GoldenFrame42CommitBatchSha256Hex, hash);
    }

    /// <summary>Stable across cultures (unlike <see cref="object.ToString"/> on records with <see cref="Vector3"/>).</summary>
    private static string InvariantCommitSnapshot(ISceneCommitRequest commit)
    {
        if (commit is SetSceneNodePropertyCommitRequest p && p.Value is Vector3 v)
            return FormattableString.Invariant($"{p.SceneKey}|{p.PropertyName}|{v.X:R9}|{v.Y:R9}|{v.Z:R9}");
        return commit.ToString() ?? "";
    }

    [Fact]
    public void Different_frame_ids_produce_different_position_commits()
    {
        var a = RunFrameCollectCommits(1L);
        var b = RunFrameCollectCommits(999L);
        Assert.Single(a);
        Assert.Single(b);
        Assert.NotEqual(a[0].ToString(), b[0].ToString());
    }

    [Fact]
    public void RecordNodeProperty_without_active_scope_throws()
    {
        var processor = new PureFrameProcessor();
        var host = new FrameProcessingHost<byte, int>(processor);
        Assert.Throws<InvalidOperationException>(() => host.RunFrame(1L));
    }

    [Fact]
    public void Nested_scope_restores_after_inner_dispose()
    {
        var sink = new CollectingSink();
        using (DeterministicWriteScope.Begin(sink, 1L))
        {
            DeterministicSceneWrites.RecordNodeProperty(PureFrameProcessor.NodeKey, nameof(Node.Position), Vector3.One);
            using (DeterministicWriteScope.Begin(sink, 2L))
            {
                DeterministicSceneWrites.RecordNodeProperty(PureFrameProcessor.NodeKey, nameof(Node.Position), Vector3.UnitZ);
            }

            DeterministicSceneWrites.RecordNodeProperty(PureFrameProcessor.NodeKey, nameof(Node.Position), Vector3.UnitX);
        }

        Assert.Equal(3, sink.Commits.Count);
    }

    private static List<ISceneCommitRequest> RunFrameCollectCommits(long frameId)
    {
        var sink = new CollectingSink();
        var processor = new PureFrameProcessor();
        var host = new FrameProcessingHost<byte, int>(processor);
        using (DeterministicWriteScope.Begin(sink, frameId))
            host.RunFrame(frameId);
        return sink.Commits;
    }
}
