using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>
/// <see cref="ScenePathResolve.TryGetNode"/> prefers <see cref="ISceneManager.GetNodeByPath"/>, then <see cref="ISceneManager.GetNode"/>.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class ScenePathResolveTests
{
    [Fact]
    public void TryGetNode_returns_same_node_for_slash_path_and_id()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        INode created;
        using (SceneMutationScope.Begin())
            created = scene.CreateNode("leaf", "owner");
        Assert.Same(created, ScenePathResolve.TryGetNode(scene, "/leaf"));
        Assert.Same(created, ScenePathResolve.TryGetNode(scene, "leaf"));
    }

    [Fact]
    public void TryGetNode_returns_null_when_no_match()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        Assert.Null(ScenePathResolve.TryGetNode(scene, "/missing"));
        Assert.Null(ScenePathResolve.TryGetNode(scene, "missing"));
    }
}
