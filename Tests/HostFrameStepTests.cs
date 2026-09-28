using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Determinism;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class HostFrameStepTests
{
    private sealed class CountingReplicaManager : IReplicaManager
    {
        public int Ticks { get; private set; }

        public void Tick() => Ticks++;

        public void Register(IReplica replica) { }

        public void Unregister(string replicaId) { }

        public IReplica? GetReplica(string replicaId) => null;

        public void OnNetworkData(string peerId, byte[] data) { }

        public string GetLocalPeerId() => "test";

        public void Subscribe(string pathPattern) { }

        public void Unsubscribe(string pathPattern) { }
    }

    private sealed class FailOnceParticipant : IDeterministicFrameParticipant
    {
        private bool _failed;

        public string ParticipantId => "fail-once";
        public FramePhase Phase => FramePhase.Publish;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            if (!_failed)
            {
                _failed = true;
                commits.Enqueue(new CreateSceneNodeCommitRequest("n", "o", Vector3.Zero));
                throw new InvalidOperationException("fail frame");
            }

            commits.Enqueue(new CreateSceneNodeCommitRequest("n", "o", Vector3.One));
        }
    }

    [Fact]
    public void RunNextFrame_failed_advances_id_skips_replica_records_failed_then_applies()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var runner = sp.GetRequiredService<IHostFrameRunner>();
        sp.GetRequiredService<IFrameParticipantRegistry>().Register(new FailOnceParticipant());
        var replicas = new CountingReplicaManager();
        sp.GetRequiredService<SceneCommitService>().SetReplication(replicas, enable: true);

        var failed = runner.RunNextFrame();
        Assert.Equal(1, failed.FrameId);
        Assert.Equal(HostFrameStatus.Failed, failed.Status);
        Assert.False(failed.Replicated);
        Assert.Equal("fail-once", failed.FailureParticipant);
        Assert.Null(sp.GetRequiredService<ISceneManager>().GetNode("n"));
        Assert.Equal(0, replicas.Ticks);

        var applied = runner.RunNextFrame();
        Assert.Equal(2, applied.FrameId);
        Assert.Equal(HostFrameStatus.Applied, applied.Status);
        Assert.True(applied.Replicated);
        Assert.Equal(1, replicas.Ticks);
        Assert.NotNull(sp.GetRequiredService<ISceneManager>().GetNode("n"));
        Assert.Equal(2, runner.Records.Count);
        Assert.Equal(HostFrameStatus.Failed, runner.Records[0].Status);
        Assert.Equal(HostFrameStatus.Applied, runner.Records[1].Status);
    }

    [Fact]
    public void RunNextFrame_network_disabled_never_ticks_replicas_on_success()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var runner = sp.GetRequiredService<IHostFrameRunner>();
        var replicas = new CountingReplicaManager();
        sp.GetRequiredService<SceneCommitService>().SetReplication(replicas, enable: false);
        var result = runner.RunNextFrame();
        Assert.Equal(HostFrameStatus.Applied, result.Status);
        Assert.False(result.Replicated);
        Assert.Equal(0, replicas.Ticks);
        Assert.Equal(1, result.FrameId);
    }

    [Fact]
    public void StopPipeline_rejects_the_next_RunNextFrame()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var runner = sp.GetRequiredService<IHostFrameRunner>();
        sp.GetRequiredService<IFrameParticipantRegistry>().Register(new FailOnceParticipant());
        sp.GetRequiredService<SceneCommitService>().FailedFramePolicy = FailedFramePolicy.StopPipeline;

        var failed = runner.RunNextFrame();
        Assert.Equal(HostFrameStatus.Failed, failed.Status);
        Assert.Throws<InvalidOperationException>(() => runner.RunNextFrame());
    }

    [Fact]
    public void Replicated_is_false_when_replica_manager_is_null()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var service = sp.GetRequiredService<SceneCommitService>();
        service.SetReplication(null, enable: true);
        var result = service.RunNextFrame();
        Assert.Equal(HostFrameStatus.Applied, result.Status);
        Assert.False(result.Replicated);
    }

    [Fact]
    public void Records_trim_to_history_limit()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var service = sp.GetRequiredService<SceneCommitService>();
        service.RecordHistoryLimit = 2;
        service.RunNextFrame();
        service.RunNextFrame();
        service.RunNextFrame();
        Assert.Equal(2, service.Records.Count);
        Assert.Equal(2, service.Records[0].FrameId);
        Assert.Equal(3, service.Records[1].FrameId);
    }
}
