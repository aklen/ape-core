using System.Numerics;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneRemoveCommitTests
{
    private static IServiceProvider BuildWithStub() => RegisteredEntityCommitTests.BuildWithStubRegistered();

    [Fact]
    public void RemoveSceneNode_commit_empties_scene()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();

        SceneCommitApplicator.Apply(scene, new CreateSceneNodeCommitRequest("n", "o", Vector3.Zero), log);
        Assert.Single(scene.GetAllNodes());

        SceneCommitApplicator.Apply(scene, new RemoveSceneNodeCommitRequest("n"), log);
        Assert.Empty(scene.GetAllNodes());
    }

    [Fact]
    public void RemoveRegisteredEntity_commit_removes_entity()
    {
        var sp = BuildWithStub();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();

        SceneCommitApplicator.Apply(scene, new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "e"), log);
        Assert.True(scene.HasEntity("e"));

        SceneCommitApplicator.Apply(scene, new RemoveRegisteredEntityCommitRequest("e"), log);
        Assert.False(scene.HasEntity("e"));
    }

    [Fact]
    public void Same_create_remove_sequence_on_two_fresh_scenes_both_empty()
    {
        static int NodeCountAfterSequence()
        {
            var sp = SceneIntegrationServices.Build();
            var scene = sp.GetRequiredService<ISceneManager>();
            var log = sp.GetRequiredService<ILogger>();
            SceneCommitApplicator.Apply(scene, new CreateSceneNodeCommitRequest("x", "o", Vector3.Zero), log);
            SceneCommitApplicator.Apply(scene, new RemoveSceneNodeCommitRequest("x"), log);
            return scene.GetAllNodes().Count();
        }

        Assert.Equal(NodeCountAfterSequence(), NodeCountAfterSequence());
        Assert.Equal(0, NodeCountAfterSequence());
    }
}
