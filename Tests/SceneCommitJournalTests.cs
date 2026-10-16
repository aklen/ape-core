using System.Numerics;
using System.Runtime.Versioning;
using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class SceneCommitJournalTests
{
    private sealed class TwoNodes : IDeterministicFrameParticipant
    {
        public string ParticipantId => "writer";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            commits.Enqueue(new CreateSceneNodeCommitRequest("lamp", "owner", Vector3.Zero));
            commits.Enqueue(new SetSceneNodePositionCommitRequest("lamp", new Vector3(1, 2, 3)));
            commits.Enqueue(new CreateSceneNodeCommitRequest("crate", "owner", Vector3.Zero));
        }
    }

    [Fact]
    public void Two_runs_record_the_same_applied_commit_order()
    {
        var first = Run();
        var second = Run();
        var expected = new[]
        {
            SceneCommitJournal.Format("writer", new CreateSceneNodeCommitRequest("lamp", "owner", Vector3.Zero)),
            SceneCommitJournal.Format("writer", new SetSceneNodePositionCommitRequest("lamp", new Vector3(1, 2, 3))),
            SceneCommitJournal.Format("writer", new CreateSceneNodeCommitRequest("crate", "owner", Vector3.Zero))
        };

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
    }

    private static string[] Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-commits-{Guid.NewGuid():N}.log");
        try
        {
            var services = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
            var host = services.GetRequiredService<SceneCommitService>();
            host.Register(new TwoNodes());
            using (var journal = SceneCommitJournal.CreateNew(path))
            {
                host.SetCommitJournal(journal);
                Assert.Equal(HostFrameStatus.Applied, host.RunNextFrame().Status);
                host.SetCommitJournal(null);
            }

            return File.ReadAllLines(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
