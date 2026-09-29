using Ape.Core.Replication;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Stable string key for commit routing (matches <see cref="ISceneManager"/> lookups).
/// </summary>
public static class SceneCommitKeys
{
    /// <summary>Prefer <see cref="IReplica.UniquePath"/> when non-empty; otherwise <see cref="IReplica.Id"/>.</summary>
    public static string FromReplica(IReplica replica)
    {
        if (!string.IsNullOrEmpty(replica.UniquePath))
            return replica.UniquePath;
        return replica.Id;
    }
}
