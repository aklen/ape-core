using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>
/// Host tick: <see cref="SceneCommitService"/> concatenates frame-scoped batches in Phase → Order → ParticipantId order.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneCommitServiceDeterminismTests
{
    private sealed class CreateThenPositionParticipant : IDeterministicFrameParticipant
    {
        private readonly string _nodeName;
        private readonly Vector3 _position;

        public CreateThenPositionParticipant(string id, int order, string nodeName, Vector3 position)
        {
            ParticipantId = id;
            Order = order;
            _nodeName = nodeName;
            _position = position;
        }

        public string ParticipantId { get; }
        public FramePhase Phase => FramePhase.Transform;
        public int Order { get; }

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            commits.Enqueue(new CreateSceneNodeCommitRequest(_nodeName, "test-owner", Vector3.Zero));
            commits.Enqueue(new SetSceneNodePositionCommitRequest(_nodeName, _position));
        }
    }

    private sealed class SetPositionParticipant : IDeterministicFrameParticipant
    {
        private readonly string _nodeName;
        private readonly Vector3 _position;

        public SetPositionParticipant(string id, int order, string nodeName, Vector3 position)
        {
            ParticipantId = id;
            Order = order;
            _nodeName = nodeName;
            _position = position;
        }

        public string ParticipantId { get; }
        public FramePhase Phase => FramePhase.Transform;
        public int Order { get; }

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits) =>
            commits.Enqueue(new SetSceneNodePositionCommitRequest(_nodeName, _position));
    }

    [Fact]
    public void RaiseHostFrame_disjoint_nodes_same_keys_produces_identical_state_on_two_fresh_pipelines()
    {
        var state1 = RunTwoParticipantsFreshScene(
            sp => new CreateThenPositionParticipant("n1-writer", 100, "n1", new Vector3(1f, 2f, 3f)),
            sp => new CreateThenPositionParticipant("n2-writer", 200, "n2", new Vector3(4f, 5f, 6f)));

        var state2 = RunTwoParticipantsFreshScene(
            sp => new CreateThenPositionParticipant("n1-writer", 100, "n1", new Vector3(1f, 2f, 3f)),
            sp => new CreateThenPositionParticipant("n2-writer", 200, "n2", new Vector3(4f, 5f, 6f)));

        Assert.Equal(state1, state2);
    }

    [Fact]
    public void Participant_order_not_register_order_determines_final_position_on_shared_node()
    {
        var spCreateFirst = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var createFirst = new CreateThenPositionParticipant("create", 100, "shared", new Vector3(1f, 0f, 0f));
        var bumpSecond = new SetPositionParticipant("bump", 200, "shared", new Vector3(10f, 0f, 0f));

        var host1 = spCreateFirst.GetRequiredService<SceneCommitService>();
        host1.Register(bumpSecond);
        host1.Register(createFirst);
        host1.RaiseHostFrame(1L);
        var posCreateThenBump = spCreateFirst.GetRequiredService<ISceneManager>().GetNode("shared")!.Position;

        var spBumpFirstKeys = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var createHigh = new CreateThenPositionParticipant("create", 200, "shared", new Vector3(1f, 0f, 0f));
        var bumpLow = new SetPositionParticipant("bump", 100, "shared", new Vector3(10f, 0f, 0f));
        var host2 = spBumpFirstKeys.GetRequiredService<SceneCommitService>();
        host2.Register(createHigh);
        host2.Register(bumpLow);
        host2.RaiseHostFrame(1L);
        var posBumpThenCreate = spBumpFirstKeys.GetRequiredService<ISceneManager>().GetNode("shared")!.Position;

        Assert.Equal(new Vector3(10f, 0f, 0f), posCreateThenBump);
        Assert.Equal(new Vector3(1f, 0f, 0f), posBumpThenCreate);
    }

    [Fact]
    public void Register_duplicate_participant_id_throws()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        host.Register(new SetPositionParticipant("same", 100, "n", Vector3.Zero));
        Assert.Throws<InvalidOperationException>(() =>
            host.Register(new SetPositionParticipant("same", 200, "n", Vector3.One)));
    }

    [Fact]
    public void Sealed_batch_rejects_enqueue_after_OnHostFrame_returns()
    {
        IFrameCommitBatch? leaked = null;
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        host.Register(new LeakBatchParticipant(b => leaked = b));
        host.RaiseHostFrame(1L);
        Assert.NotNull(leaked);
        Assert.Throws<InvalidOperationException>(() =>
            leaked!.Enqueue(new SetSceneNodePositionCommitRequest("x", Vector3.Zero)));
    }

    [Fact]
    public void Participant_exception_after_enqueue_discards_frame_and_same_host_applies_next_frame()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        var scene = sp.GetRequiredService<ISceneManager>();
        var failOnce = new FailOnceParticipant("n", new Vector3(1, 2, 3), new Vector3(4, 5, 6));
        host.Register(failOnce);

        Assert.False(host.RaiseHostFrame(1L));
        Assert.Null(scene.GetNode("n"));

        Assert.True(host.RaiseHostFrame(2L));
        Assert.Equal(new Vector3(4, 5, 6), scene.GetNode("n")!.Position);
        Assert.Equal(2, failOnce.Calls);
        var records = sp.GetRequiredService<IHostFrameRunner>().Records;
        Assert.Equal(HostFrameStatus.Failed, records[0].Status);
        Assert.Equal(HostFrameStatus.Applied, records[1].Status);
    }

    [Fact]
    public void Failed_frame_does_not_apply_earlier_participant_ops()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        var scene = sp.GetRequiredService<ISceneManager>();
        host.Register(new CreateThenPositionParticipant("first", 100, "keep", new Vector3(9, 9, 9)));
        host.Register(new EnqueueThenThrowParticipant("second", 200, "other", Vector3.One));
        Assert.False(host.RaiseHostFrame(1L));
        Assert.Null(scene.GetNode("keep"));
        Assert.Null(scene.GetNode("other"));
    }

    [Fact]
    public void Enqueue_from_background_thread_during_OnHostFrame_throws()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        var probe = new BackgroundEnqueueParticipant();
        host.Register(probe);
        Assert.True(host.RaiseHostFrame(1L));
        Assert.IsType<InvalidOperationException>(probe.BackgroundError);
        Assert.Contains("host thread", probe.BackgroundError!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Strict_multi_writer_discards_frame()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        sp.GetRequiredService<SceneCommitService>().MultiWriterMode = SceneMultiWriterMode.Strict;
        var scene = sp.GetRequiredService<ISceneManager>();
        host.Register(new CreateThenPositionParticipant("a", 100, "shared", new Vector3(1, 0, 0)));
        host.Register(new SetPositionParticipant("b", 200, "shared", new Vector3(2, 0, 0)));
        Assert.False(host.RaiseHostFrame(1L));
        Assert.Null(scene.GetNode("shared"));
    }

    [Fact]
    public void Same_frame_id_passes_identical_logical_time_to_participants()
    {
        DateTimeOffset? t1 = null;
        DateTimeOffset? t2 = null;
        var sp1 = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var h1 = sp1.GetRequiredService<SceneCommitService>();
        h1.Register(new CaptureTimeParticipant("t", t => t1 = t));
        h1.RaiseHostFrame(17L);
        var sp2 = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var h2 = sp2.GetRequiredService<SceneCommitService>();
        h2.Register(new CaptureTimeParticipant("t", t => t2 = t));
        h2.RaiseHostFrame(17L);
        Assert.Equal(t1, t2);
        Assert.Equal(LogicalFrameTime.FromFrameId(17L), t1);
    }

    private sealed class EnqueueThenThrowParticipant : IDeterministicFrameParticipant
    {
        private readonly string _nodeName;
        private readonly Vector3 _position;

        public EnqueueThenThrowParticipant(string id, int order, string nodeName, Vector3 position)
        {
            ParticipantId = id;
            Order = order;
            _nodeName = nodeName;
            _position = position;
        }

        public string ParticipantId { get; }
        public FramePhase Phase => FramePhase.Transform;
        public int Order { get; }

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            commits.Enqueue(new CreateSceneNodeCommitRequest(_nodeName, "test-owner", Vector3.Zero));
            commits.Enqueue(new SetSceneNodePositionCommitRequest(_nodeName, _position));
            throw new InvalidOperationException("simulated participant failure");
        }
    }

    private sealed class FailOnceParticipant : IDeterministicFrameParticipant
    {
        private readonly string _nodeName;
        private readonly Vector3 _failPosition;
        private readonly Vector3 _okPosition;
        private bool _failed;

        public FailOnceParticipant(string nodeName, Vector3 failPosition, Vector3 okPosition)
        {
            _nodeName = nodeName;
            _failPosition = failPosition;
            _okPosition = okPosition;
        }

        public int Calls { get; private set; }
        public string ParticipantId => "fail-once";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 100;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            Calls++;
            if (!_failed)
            {
                _failed = true;
                commits.Enqueue(new CreateSceneNodeCommitRequest(_nodeName, "test-owner", Vector3.Zero));
                commits.Enqueue(new SetSceneNodePositionCommitRequest(_nodeName, _failPosition));
                throw new InvalidOperationException("first frame fails");
            }

            commits.Enqueue(new CreateSceneNodeCommitRequest(_nodeName, "test-owner", Vector3.Zero));
            commits.Enqueue(new SetSceneNodePositionCommitRequest(_nodeName, _okPosition));
        }
    }

    private sealed class BackgroundEnqueueParticipant : IDeterministicFrameParticipant
    {
        public Exception? BackgroundError { get; private set; }
        public string ParticipantId => "bg";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            Exception? err = null;
            var thread = new Thread(() =>
            {
                try
                {
                    commits.Enqueue(new SetSceneNodePositionCommitRequest("x", Vector3.Zero));
                }
                catch (Exception ex)
                {
                    err = ex;
                }
            });
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            BackgroundError = err;
        }
    }

    private sealed class CaptureTimeParticipant : IDeterministicFrameParticipant
    {
        private readonly Action<DateTimeOffset> _capture;

        public CaptureTimeParticipant(string id, Action<DateTimeOffset> capture)
        {
            ParticipantId = id;
            _capture = capture;
        }

        public string ParticipantId { get; }
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits) =>
            _capture(context.LogicalTime);
    }

    private sealed class LeakBatchParticipant : IDeterministicFrameParticipant
    {
        private readonly Action<IFrameCommitBatch> _capture;

        public LeakBatchParticipant(Action<IFrameCommitBatch> capture) => _capture = capture;

        public string ParticipantId => "leak";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits) => _capture(commits);
    }

    private static (Vector3 N1, Vector3 N2) RunTwoParticipantsFreshScene(
        Func<IServiceProvider, IDeterministicFrameParticipant> p1,
        Func<IServiceProvider, IDeterministicFrameParticipant> p2)
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var host = sp.GetRequiredService<SceneCommitService>();
        host.Register(p1(sp));
        host.Register(p2(sp));
        host.RaiseHostFrame(1L);
        var scene = sp.GetRequiredService<ISceneManager>();
        return (scene.GetNode("n1")!.Position, scene.GetNode("n2")!.Position);
    }
}
