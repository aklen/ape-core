using Ape.Core.Replication;

namespace Ape.Core.Scene;

/// <summary>
/// Write-capable scene graph. Core applicator and remaining legacy code.
/// Migrated plugins should depend on <see cref="ISceneRead"/>, not this type.
/// </summary>
public interface ISceneManager : ISceneRead
{
    /// <summary>
    /// Create a new scene node.
    /// All properties (Position, Orientation, Scale, etc.) should be set via property setters after creation.
    /// </summary>
    INode CreateNode(string name, string? ownerId = null);

    ReplicaLock<INode> LockNode(string nodeId, int timeoutMs = 5000);

    ReplicaLock<INode> LockNodeByPath(string uniquePath, int timeoutMs = 5000);

    void RemoveNode(string nodeId);

    /// <summary>
    /// Creates an entity using a <c>typeId</c> registered with <see cref="ISceneEntityRegistry"/>.
    /// </summary>
    IEntity CreateRegisteredEntity(string typeId, string name, string? ownerId = null);

    bool RemoveEntity(string name);
}
