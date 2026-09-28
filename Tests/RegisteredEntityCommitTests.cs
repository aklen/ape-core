using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>
/// End-to-end <see cref="CreateRegisteredEntityCommitRequest"/> / <see cref="SetSceneEntityPropertyCommitRequest"/>
/// with a minimal <see cref="Entity"/> registered at runtime in <see cref="ISceneEntityRegistry"/>.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class RegisteredEntityCommitTests
{
    internal static IServiceProvider BuildWithStubRegistered()
    {
        var sp = SceneIntegrationServices.Build();
        var registry = sp.GetRequiredService<ISceneEntityRegistry>();
        registry.Register(
            TestStubEntity.RegistryTypeId,
            static (svc, entityId, ownerId) => new TestStubEntity { Id = entityId });
        return sp;
    }

    [Fact]
    public void CreateRegisteredEntity_commit_creates_entity_in_scene()
    {
        var sp = BuildWithStubRegistered();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();

        SceneCommitApplicator.Apply(scene, new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "e1"), log);

        var e = scene.GetEntity("e1");
        Assert.NotNull(e);
        Assert.IsType<TestStubEntity>(e);
        Assert.Equal(TestStubEntity.RegistryTypeId, e.TypeId);
    }

    [Fact]
    public void Set_entity_property_commit_updates_Key_property()
    {
        var sp = BuildWithStubRegistered();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();

        SceneCommitApplicator.Apply(scene, new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "e2"), log);
        SceneCommitApplicator.Apply(
            scene,
            new SetSceneEntityPropertyCommitRequest("e2", nameof(TestStubEntity.Counter), 7),
            log);

        var stub = Assert.IsType<TestStubEntity>(scene.GetEntity("e2"));
        Assert.Equal(7, stub.Counter);
    }

    [Fact]
    public void Two_fresh_scenes_same_entity_commits_yield_same_counter()
    {
        static int RunPipeline()
        {
            var sp = BuildWithStubRegistered();
            var scene = sp.GetRequiredService<ISceneManager>();
            var log = sp.GetRequiredService<ILogger>();
            SceneCommitApplicator.Apply(scene, new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "x"), log);
            SceneCommitApplicator.Apply(
                scene,
                new SetSceneEntityPropertyCommitRequest("x", nameof(TestStubEntity.Counter), 42),
                log);
            return Assert.IsType<TestStubEntity>(scene.GetEntity("x")).Counter;
        }

        Assert.Equal(RunPipeline(), RunPipeline());
    }
}
