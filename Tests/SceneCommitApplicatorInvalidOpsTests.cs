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
/// Invalid or no-op commits must not throw and must leave the scene in a predictable state (deterministic failure path).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneCommitApplicatorInvalidOpsTests
{
    /// <summary>Not handled by <see cref="SceneCommitApplicator"/> — exercises the default branch.</summary>
    private sealed class UnknownCommitRequest : ISceneCommitRequest
    {
    }

    [Fact]
    public void Set_position_on_missing_node_twice_does_not_throw_and_leaves_scene_empty()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        var op = new SetSceneNodePositionCommitRequest("no-such-node", Vector3.One);
        SceneCommitApplicator.Apply(scene, op, log);
        SceneCommitApplicator.Apply(scene, op, log);
        Assert.Empty(scene.GetAllNodes());
    }

    [Fact]
    public void Set_node_property_on_missing_key_does_not_throw_and_leaves_scene_empty()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        var op = new SetSceneNodePropertyCommitRequest("/missing", nameof(Node.Position), Vector3.UnitY);
        SceneCommitApplicator.Apply(scene, op, log);
        Assert.Empty(scene.GetAllNodes());
    }

    [Fact]
    public void Remove_missing_node_does_not_throw()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(scene, new RemoveSceneNodeCommitRequest("ghost"), log);
        Assert.Empty(scene.GetAllNodes());
    }

    [Fact]
    public void Unknown_commit_request_type_does_not_throw_and_does_not_create_nodes()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(scene, new UnknownCommitRequest(), log);
        Assert.Empty(scene.GetAllNodes());
    }

    [Fact]
    public void Valid_create_then_invalid_updates_twice_yield_same_final_state_as_single_valid_create()
    {
        var sp1 = SceneIntegrationServices.Build();
        var scene1 = sp1.GetRequiredService<ISceneManager>();
        var log1 = sp1.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(scene1, new CreateSceneNodeCommitRequest("n", "o", Vector3.Zero), log1);
        SceneCommitApplicator.Apply(scene1, new SetSceneNodePositionCommitRequest("n", new Vector3(2f, 0f, 0f)), log1);
        SceneCommitApplicator.Apply(scene1, new SetSceneNodePositionCommitRequest("missing", Vector3.One), log1);
        SceneCommitApplicator.Apply(scene1, new SetSceneNodePositionCommitRequest("missing", Vector3.One), log1);

        var sp2 = SceneIntegrationServices.Build();
        var scene2 = sp2.GetRequiredService<ISceneManager>();
        var log2 = sp2.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(scene2, new CreateSceneNodeCommitRequest("n", "o", Vector3.Zero), log2);
        SceneCommitApplicator.Apply(scene2, new SetSceneNodePositionCommitRequest("n", new Vector3(2f, 0f, 0f)), log2);

        Assert.Equal(scene1.GetNode("n")!.Position, scene2.GetNode("n")!.Position);
    }
}
