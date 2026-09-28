using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>
/// Same <see cref="ISceneCommitRequest"/> sequence applied to two fresh scenes must yield the same node state
/// (<see cref="SceneCommitApplicator"/> is pure with respect to scene identity).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneCommitApplicatorDeterminismTests
{
    private static void ApplyAll(ISceneManager scene, ILogger logger, IReadOnlyList<ISceneCommitRequest> commits)
    {
        foreach (var op in commits)
            SceneCommitApplicator.Apply(scene, op, logger);
    }

    [Fact]
    public void Identical_commit_sequence_twice_yields_identical_node_positions()
    {
        var commits = new List<ISceneCommitRequest>
        {
            new CreateSceneNodeCommitRequest("alpha", "peer-test", Vector3.Zero),
            new SetSceneNodePositionCommitRequest("alpha", new Vector3(1f, 2f, 3f)),
            new SetSceneNodePropertyCommitRequest("/alpha", nameof(Node.Orientation), Quaternion.CreateFromYawPitchRoll(0.2f, 0.3f, 0.4f)),
        };

        var sp1 = SceneIntegrationServices.Build();
        var scene1 = sp1.GetRequiredService<ISceneManager>();
        var log1 = sp1.GetRequiredService<ILogger>();
        ApplyAll(scene1, log1, commits);
        var node1 = scene1.GetNode("alpha");
        Assert.NotNull(node1);

        var sp2 = SceneIntegrationServices.Build();
        var scene2 = sp2.GetRequiredService<ISceneManager>();
        var log2 = sp2.GetRequiredService<ILogger>();
        ApplyAll(scene2, log2, commits);
        var node2 = scene2.GetNode("alpha");
        Assert.NotNull(node2);

        Assert.Equal(node1!.Position, node2!.Position);
        Assert.Equal(node1.Orientation, node2.Orientation);
    }
}
