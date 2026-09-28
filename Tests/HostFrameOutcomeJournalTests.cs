using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;

namespace Ape.Core.Tests;

public sealed class HostFrameOutcomeJournalTests
{
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
                throw new InvalidOperationException("pilot failure");
            }
        }
    }

    private sealed class PartialApplyParticipant : IDeterministicFrameParticipant
    {
        public string ParticipantId => "partial-apply";
        public FramePhase Phase => FramePhase.Publish;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            commits.Enqueue(new CreateSceneNodeCommitRequest("partial-node", "test", Vector3.One));
            commits.Enqueue(new CreateRegisteredEntityCommitRequest("unknown.entity.type.v1", "missing"));
        }
    }

    private sealed class SharedPropertyWriter(string id) : IDeterministicFrameParticipant
    {
        public string ParticipantId => id;
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits) =>
            commits.Enqueue(new SetSceneNodePropertyCommitRequest("same-node", "Position", Vector3.One));
    }

    [Fact]
    public void Host_runner_persists_failed_and_applied_outcomes_and_aligns_with_samples()
    {
        var outcomePath = Path.Combine(Path.GetTempPath(), $"ape-outcome-{Guid.NewGuid():N}.jsonl");
        var samplePath = Path.Combine(Path.GetTempPath(), $"ape-sample-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
            var service = sp.GetRequiredService<SceneCommitService>();
            using (var outcomeWriter = HostFrameOutcomeJournal.CreateNew(outcomePath))
            {
                service.SetOutcomeJournal(outcomeWriter);
                service.Register(new FailOnceParticipant());
                Assert.Equal(HostFrameStatus.Failed, service.RunNextFrame().Status);
                Assert.Equal(HostFrameStatus.Applied, service.RunNextFrame().Status);
                service.SetOutcomeJournal(null);
            }

            using (var sampleWriter = SampleFrameJournal.CreateNew(samplePath,
                new SampleFrameJournalHeader(LogicalFrameTime.Epoch, LogicalFrameTime.DefaultFrameDuration)))
            {
                sampleWriter.Append(new SampleFrame(1, LogicalFrameTime.FromFrameId(1), []));
                sampleWriter.Append(new SampleFrame(2, LogicalFrameTime.FromFrameId(2), []));
            }

            var outcomes = HostFrameOutcomeJournal.Read(outcomePath);
            Assert.Equal([HostFrameStatus.Failed, HostFrameStatus.Applied],
                outcomes.Select(o => o.Status).ToArray());
            Assert.Equal("fail-once", outcomes[0].FailureParticipant);
            HostFrameOutcomeJournal.VerifyAlignment(SampleFrameJournal.Read(samplePath).Frames, outcomes);
        }
        finally
        {
            File.Delete(outcomePath);
            File.Delete(samplePath);
        }
    }

    [Fact]
    public void Alignment_rejects_missing_or_shifted_outcomes()
    {
        var sample = new SampleFrame(4, LogicalFrameTime.FromFrameId(4), []);
        Assert.Throws<InvalidDataException>(() =>
            HostFrameOutcomeJournal.VerifyAlignment([sample], []));
        Assert.Throws<InvalidDataException>(() =>
            HostFrameOutcomeJournal.VerifyAlignment([sample], [
                new HostFrameRecord(5, sample.LogicalTime, HostFrameStatus.Applied)]));
    }

    [Fact]
    public void Writer_rejects_reused_frame_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-outcome-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var writer = HostFrameOutcomeJournal.CreateNew(path);
            var record = new HostFrameRecord(1, LogicalFrameTime.FromFrameId(1), HostFrameStatus.Discarded);
            writer.Append(record);
            Assert.Throws<InvalidDataException>(() => writer.Append(record));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Applicator_failure_records_possible_partial_scene_and_halts_host()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-outcome-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
            var service = sp.GetRequiredService<SceneCommitService>();
            using (var writer = HostFrameOutcomeJournal.CreateNew(path))
            {
                service.SetOutcomeJournal(writer);
                service.Register(new PartialApplyParticipant());
                var result = service.RunNextFrame();
                Assert.Equal(HostFrameStatus.Failed, result.Status);
                Assert.True(result.MayHavePartialSceneWrites);
                Assert.Equal("*applicator*", result.FailureParticipant);
                Assert.NotNull(sp.GetRequiredService<Ape.Core.Scene.ISceneManager>().GetNode("partial-node"));
                Assert.Throws<InvalidOperationException>(() => service.RunNextFrame());
                service.SetOutcomeJournal(null);
            }

            var recorded = Assert.Single(HostFrameOutcomeJournal.Read(path));
            Assert.True(recorded.MayHavePartialSceneWrites);
            Assert.Equal(HostFrameStatus.Failed, recorded.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Strict_multi_writer_discard_is_persisted_without_partial_scene_flag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-outcome-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
            var service = sp.GetRequiredService<SceneCommitService>();
            service.MultiWriterMode = SceneMultiWriterMode.Strict;
            using (var writer = HostFrameOutcomeJournal.CreateNew(path))
            {
                service.SetOutcomeJournal(writer);
                service.Register(new SharedPropertyWriter("a"));
                service.Register(new SharedPropertyWriter("b"));
                var result = service.RunNextFrame();
                Assert.Equal(HostFrameStatus.Discarded, result.Status);
                Assert.False(result.MayHavePartialSceneWrites);
                service.SetOutcomeJournal(null);
            }

            var recorded = Assert.Single(HostFrameOutcomeJournal.Read(path));
            Assert.Equal(HostFrameStatus.Discarded, recorded.Status);
            Assert.False(recorded.MayHavePartialSceneWrites);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
