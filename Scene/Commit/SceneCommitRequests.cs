using System.Numerics;
using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

// --- INode (transform hierarchy) — fully owned by Ape.Core scene API ---

/// <summary>Create a transform node (see <see cref="ISceneManager.CreateNode"/>).</summary>
public sealed record CreateSceneNodeCommitRequest(string NodeName, string? OwnerId, Vector3 InitialPosition) : ISceneCommitRequest;

/// <summary>Set world position on an existing node by id.</summary>
public sealed record SetSceneNodePositionCommitRequest(string NodeId, Vector3 Position) : ISceneCommitRequest;

/// <summary>Remove a node by id.</summary>
public sealed record RemoveSceneNodeCommitRequest(string NodeId) : ISceneCommitRequest;

/// <summary>
/// Set a single [Key] property on an existing node by path or id (<paramref name="SceneKey"/>).
/// Applied via <see cref="ReplicaPropertyBinder"/> (reflection + cache).
/// </summary>
public sealed record SetSceneNodePropertyCommitRequest(string SceneKey, string PropertyName, object? Value) : ISceneCommitRequest;

// --- IEntity (registry factories) — Core only knows typeId + name; no module DTOs ---

/// <summary>
/// Create an entity via <see cref="ISceneManager.CreateRegisteredEntity"/> (module must have registered <paramref name="TypeId"/>).
/// Does not set <c>Model</c>; modules enqueue further requests or apply logic after create.
/// </summary>
public sealed record CreateRegisteredEntityCommitRequest(string TypeId, string Name, string? OwnerId = null) : ISceneCommitRequest;

/// <summary>Remove an entity by unique name (see <see cref="ISceneManager.RemoveEntity"/>).</summary>
public sealed record RemoveRegisteredEntityCommitRequest(string Name) : ISceneCommitRequest;

/// <summary>
/// Set a single [Key] property on a registered entity by its scene key (name/path used with <see cref="ISceneManager.GetEntity"/>).
/// Applied via <see cref="ReplicaPropertyBinder"/>.
/// </summary>
public sealed record SetSceneEntityPropertyCommitRequest(string SceneKey, string PropertyName, object? Value) : ISceneCommitRequest;
