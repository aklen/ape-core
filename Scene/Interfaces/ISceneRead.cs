namespace Ape.Core.Scene;

/// <summary>
/// Lookups for plugins and observers. Mutations go through <c>IFrameCommitBatch</c>.
/// Setters on returned nodes/entities throw <see cref="SceneMutationOutsideScopeException"/>
/// unless Core apply scope is active. Remaining bypass on non-snapshot models: in-place mutation
/// of mutable reference-valued properties, including collections and nested model objects.
/// <c>GnssSatelliteState</c> is a frozen snapshot; replace the whole <c>Model</c> via commit.
/// Plugin DI is Core allowlist + <c>Ape.Module.*</c> name prefix (host-loaded modules only).
/// </summary>
public interface ISceneRead
{
    INode? GetNode(string nodeId);

    INode? GetNodeByPath(string uniquePath);

    IEnumerable<INode> GetAllNodes();

    IEntity? GetEntity(string name);

    T? GetEntity<T>(string name) where T : class, IEntity;

    IEnumerable<T> GetEntities<T>() where T : class, IEntity;

    IEnumerable<IEntity> GetAllEntities();

    bool HasEntity(string name);
}
