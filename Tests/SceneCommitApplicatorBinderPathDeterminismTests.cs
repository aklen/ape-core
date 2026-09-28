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
/// <see cref="SetSceneNodePropertyCommitRequest"/> goes through <see cref="ReplicaPropertyBinder"/> for each
/// <see cref="MessagePack.KeyAttribute"/> property on <see cref="Node"/> — replay must yield the same replica snapshot on a fresh scene.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneCommitApplicatorBinderPathDeterminismTests
{
    private static void ApplyAll(ISceneManager scene, ILogger logger, IReadOnlyList<ISceneCommitRequest> commits)
    {
        foreach (var op in commits)
            SceneCommitApplicator.Apply(scene, op, logger);
    }

    private static void AssertNodeKeyPropertiesEqual(INode expected, INode actual)
    {
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Orientation, actual.Orientation);
        Assert.Equal(expected.Scale, actual.Scale);
        Assert.Equal(expected.IsVisible, actual.IsVisible);
        Assert.Equal(expected.ChildrenVisible, actual.ChildrenVisible);
    }

    [Fact]
    public void Property_commits_covering_all_Node_Key_fields_twice_yield_identical_state()
    {
        var quat = Quaternion.CreateFromYawPitchRoll(0.11f, 0.22f, 0.33f);
        var commits = new List<ISceneCommitRequest>
        {
            new CreateSceneNodeCommitRequest("n", "owner", Vector3.Zero),
            new SetSceneNodePropertyCommitRequest("/n", nameof(Node.Position), new Vector3(9f, 8f, 7f)),
            new SetSceneNodePropertyCommitRequest("/n", nameof(Node.Orientation), quat),
            new SetSceneNodePropertyCommitRequest("/n", nameof(Node.Scale), new Vector3(1.25f, 0.5f, 2f)),
            new SetSceneNodePropertyCommitRequest("/n", nameof(Node.IsVisible), "false"),
            new SetSceneNodePropertyCommitRequest("/n", nameof(Node.ChildrenVisible), false),
        };

        var sp1 = SceneIntegrationServices.Build();
        var scene1 = sp1.GetRequiredService<ISceneManager>();
        ApplyAll(scene1, sp1.GetRequiredService<ILogger>(), commits);
        var node1 = scene1.GetNode("n");
        Assert.NotNull(node1);

        var sp2 = SceneIntegrationServices.Build();
        var scene2 = sp2.GetRequiredService<ISceneManager>();
        ApplyAll(scene2, sp2.GetRequiredService<ILogger>(), commits);
        var node2 = scene2.GetNode("n");
        Assert.NotNull(node2);

        AssertNodeKeyPropertiesEqual(node1!, node2!);
    }
}
