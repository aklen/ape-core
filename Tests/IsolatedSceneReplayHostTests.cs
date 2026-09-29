using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;

namespace Ape.Core.Tests;

public sealed class IsolatedSceneReplayHostTests
{
    private sealed class CreateNodeParticipant : IDeterministicFrameParticipant
    {
        public string ParticipantId => "replay-test";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits) =>
            commits.Enqueue(new CreateSceneNodeCommitRequest("node", "replay", Vector3.One));
    }

    [Fact]
    public void Runs_only_its_own_scene_and_requires_contiguous_recorded_frames()
    {
        var registered = false;
        using var replay = new IsolatedSceneReplayHost(_ => registered = true);
        Assert.True(registered);
        var before = SceneStateDigest.Capture(replay.Scene);
        replay.Register(new CreateNodeParticipant());
        var sample = new SampleFrame(1, LogicalFrameTime.FromFrameId(1), []);

        var result = replay.Run(sample);

        Assert.Equal(HostFrameStatus.Applied, result.Status);
        Assert.False(result.Replicated);
        Assert.NotEqual(before, SceneStateDigest.Capture(replay.Scene));
        Assert.Single(replay.Records);
        Assert.Throws<InvalidDataException>(() => replay.Run(sample));
    }

    [Fact]
    public void First_recorded_frame_can_start_after_the_production_host_began()
    {
        using var replay = new IsolatedSceneReplayHost();
        replay.Register(new CreateNodeParticipant());
        var first = new SampleFrame(42, LogicalFrameTime.FromFrameId(42), []);

        Assert.Equal(42, replay.Run(first).FrameId);
        Assert.Throws<InvalidDataException>(() => replay.Run(
            new SampleFrame(44, LogicalFrameTime.FromFrameId(44), [])));
        Assert.Single(replay.Records);
    }
}
