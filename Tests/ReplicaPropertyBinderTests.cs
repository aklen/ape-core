using System.Numerics;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class ReplicaPropertyBinderTests
{
    [Fact]
    public void TrySet_succeeds_for_Key_property_with_compatible_value()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
        {
            var node = scene.CreateNode("bind-test", "o");
            Assert.True(ReplicaPropertyBinder.TrySet(node, nameof(Node.Position), new Vector3(3f, 4f, 5f), null));
            Assert.Equal(new Vector3(3f, 4f, 5f), node.Position);
        }
    }

    [Fact]
    public void TrySet_returns_false_for_unknown_property_and_does_not_throw()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
        {
            var node = scene.CreateNode("bind-test", "o");
            var before = node.Position;
            Assert.False(ReplicaPropertyBinder.TrySet(node, "NotAKeyProperty", 1f, null));
            Assert.Equal(before, node.Position);
        }
    }

    [Fact]
    public void TrySet_returns_false_for_null_value_on_non_nullable_value_type()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
        {
            var node = scene.CreateNode("bind-test", "o");
            var before = node.Position;
            Assert.False(ReplicaPropertyBinder.TrySet(node, nameof(Node.Position), null, null));
            Assert.Equal(before, node.Position);
        }
    }

    [Fact]
    public void TrySet_coerces_string_to_bool_via_invariant_conversion()
    {
        var sp = SceneIntegrationServices.Build();
        var scene = sp.GetRequiredService<ISceneManager>();
        using (SceneMutationScope.Begin())
        {
            var node = scene.CreateNode("bind-test", "o");
            Assert.True(node.IsVisible);
            Assert.True(ReplicaPropertyBinder.TrySet(node, nameof(Node.IsVisible), "false", null));
            Assert.False(node.IsVisible);
        }
    }
}
