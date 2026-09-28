namespace Ape.Core.Scene.Commit;

/// <summary>
/// Resolves scene objects by <see cref="Ape.Core.Replication.IReplica.UniquePath"/> or by id string (same as <see cref="ISceneManager.GetNode"/>).
/// </summary>
public static class ScenePathResolve
{
    /// <summary>Try path first, then id.</summary>
    public static INode? TryGetNode(ISceneManager scene, string sceneKey)
    {
        return scene.GetNodeByPath(sceneKey) ?? scene.GetNode(sceneKey);
    }
}
