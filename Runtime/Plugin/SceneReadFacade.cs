using Ape.Core.Scene;

namespace Ape.Core.Runtime.Plugin;

/// <summary>
/// Plugin-facing <see cref="ISceneRead"/> that is not <see cref="ISceneManager"/>, so a cast cannot recover the writer.
/// Lookups still return live nodes/entities; property setters on those objects remain a legacy bypass.
/// </summary>
internal sealed class SceneReadFacade : ISceneRead
{
    private readonly ISceneRead _inner;

    public SceneReadFacade(ISceneRead inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public INode? GetNode(string nodeId) => _inner.GetNode(nodeId);

    public INode? GetNodeByPath(string uniquePath) => _inner.GetNodeByPath(uniquePath);

    public IEnumerable<INode> GetAllNodes() => _inner.GetAllNodes();

    public IEntity? GetEntity(string name) => _inner.GetEntity(name);

    public T? GetEntity<T>(string name) where T : class, IEntity => _inner.GetEntity<T>(name);

    public IEnumerable<T> GetEntities<T>() where T : class, IEntity => _inner.GetEntities<T>();

    public IEnumerable<IEntity> GetAllEntities() => _inner.GetAllEntities();

    public bool HasEntity(string name) => _inner.HasEntity(name);
}
