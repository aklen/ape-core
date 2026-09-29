using System.Numerics;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>
/// <see cref="SceneCommitApplicator"/> + entity registry and <see cref="ReplicaPropertyBinder"/>-backed property sets.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneCommitApplicatorEntityAndBinderTests
{
    [Fact]
    public void CreateRegisteredEntity_unknown_type_id_throws_ArgumentException_deterministically()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        var req = new CreateRegisteredEntityCommitRequest("unknown.entity.type.v1", "e1");

        var ex1 = Assert.Throws<ArgumentException>(() => SceneCommitApplicator.Apply(scene, req, log));
        var ex2 = Assert.Throws<ArgumentException>(() => SceneCommitApplicator.Apply(scene, req, log));

        Assert.Equal(ex1.ParamName, ex2.ParamName);
        Assert.Equal(ex1.Message, ex2.Message);
    }

    [Fact]
    public void Set_node_property_invalid_Key_name_leaves_position_unchanged()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        var log = sp.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(scene, new CreateSceneNodeCommitRequest("n", "o", new Vector3(1f, 2f, 3f)), log);
        var before = scene.GetNode("n")!.Position;

        SceneCommitApplicator.Apply(
            scene,
            new SetSceneNodePropertyCommitRequest("/n", "NotAKeyProperty", 99f),
            log);

        Assert.Equal(before, scene.GetNode("n")!.Position);
    }
}
