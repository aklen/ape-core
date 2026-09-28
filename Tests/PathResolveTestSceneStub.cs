using System.Numerics;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Models;

namespace Ape.Core.Tests;

/// <summary>
/// Minimal <see cref="ISceneManager"/> for <see cref="ScenePathResolve"/> unit tests — only path/id lookups are real.
/// </summary>
internal sealed class PathResolveTestSceneStub : ISceneManager
{
    private readonly INode? _pathNode;
    private readonly INode? _idNode;

    /// <summary>
    /// <paramref name="pathNode"/> returned for <see cref="GetNodeByPath"/> when the key matches <paramref name="pathKey"/>.
    /// <paramref name="idNode"/> returned for <see cref="GetNode"/> when the key matches <paramref name="idKey"/>.
    /// </summary>
    public PathResolveTestSceneStub(string pathKey, INode pathNode, string idKey, INode idNode)
    {
        PathKey = pathKey;
        IdKey = idKey;
        _pathNode = pathNode;
        _idNode = idNode;
    }

    public string PathKey { get; }
    public string IdKey { get; }

    public INode? GetNodeByPath(string uniquePath) =>
        uniquePath == PathKey ? _pathNode : null;

    public INode? GetNode(string nodeId) =>
        nodeId == IdKey ? _idNode : null;

    public INode CreateNode(string name, string? ownerId = null) =>
        throw new NotSupportedException();

    public ReplicaLock<INode> LockNode(string nodeId, int timeoutMs = 5000) =>
        new ReplicaLock<INode>(GetNode(nodeId), timeoutMs);

    public ReplicaLock<INode> LockNodeByPath(string uniquePath, int timeoutMs = 5000) =>
        new ReplicaLock<INode>(GetNodeByPath(uniquePath), timeoutMs);

    public void RemoveNode(string nodeId) => throw new NotSupportedException();

    public IEnumerable<INode> GetAllNodes() => Array.Empty<INode>();

    public IEntity CreateRegisteredEntity(string typeId, string name, string? ownerId = null) =>
        throw new NotSupportedException();

    public IEntity? GetEntity(string name) => null;

    public T? GetEntity<T>(string name) where T : class, IEntity => null;

    public IEnumerable<T> GetEntities<T>() where T : class, IEntity => Array.Empty<T>();

    public IEnumerable<IEntity> GetAllEntities() => Array.Empty<IEntity>();

    public bool RemoveEntity(string name) => false;

    public bool HasEntity(string name) => false;
}
