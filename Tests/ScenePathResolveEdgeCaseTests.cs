using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Scene.Models;

namespace Ape.Core.Tests;

/// <summary>
/// <see cref="ScenePathResolve.TryGetNode"/> tries path first, then id — keys that look like paths vs ids can resolve to different nodes.
/// </summary>
public class ScenePathResolveEdgeCaseTests
{
    [Fact]
    public void TryGetNode_path_key_returns_path_node_when_path_and_id_differ()
    {
        var pathNode = new Node { Id = "path-id" };
        var idNode = new Node { Id = "foo" };
        var scene = new PathResolveTestSceneStub("/foo", pathNode, "foo", idNode);

        Assert.Same(pathNode, ScenePathResolve.TryGetNode(scene, "/foo"));
    }

    [Fact]
    public void TryGetNode_id_key_returns_id_node_when_path_lookup_misses()
    {
        var pathNode = new Node { Id = "path-id" };
        var idNode = new Node { Id = "foo" };
        var scene = new PathResolveTestSceneStub("/foo", pathNode, "foo", idNode);

        Assert.Same(idNode, ScenePathResolve.TryGetNode(scene, "foo"));
    }
}
