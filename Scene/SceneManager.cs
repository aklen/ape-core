using Ape.Core.Logging;
using Ape.Core.Event;
using Ape.Core.Event.Models;
using Ape.Core.Replication;
using Ape.Core.Replication.Services;
using Ape.Core.Runtime.Service;
using Ape.Core.Scene.Models;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Ape.Core.Scene;

/// <summary>
/// Scene manager implementation that creates and manages scene objects.
/// All scene objects are Replica instances and automatically replicate across the network.
/// Maintains both ID-based and path-based lookup for efficient retrieval.
/// Manages Nodes (transform) and entities as <see cref="IEntity"/>; concrete types are created via <see cref="ISceneEntityRegistry"/>.
/// Also implements ICoreService for direct registration in the DI container.
/// </summary>
public class SceneManager : ISceneManager, ICoreService
{
    public string ServiceId => "core-scene-manager";
    public string Name => "Scene Manager";
    
    // Clean storage architecture:
    // _nodes: Transform hierarchy (INode with parent-child relationships)
    // _entities: Functional objects (IEntity; no hierarchy)
    private readonly ConcurrentDictionary<string, INode> _nodes = new();
    private readonly ConcurrentDictionary<string, INode> _nodesByPath = new();
    private readonly ConcurrentDictionary<string, IEntity> _entities = new();
    private readonly object _pathGenerationLock = new object();
    private IReplicaManager _replicaManager = null!;  // Set in Initialize()
    private IEventManager _eventManager = null!;      // Set in Initialize()
    private ILogger _logger = null!;                   // Set in Initialize()
    private IServiceProvider _services = null!;
    private ISceneEntityRegistry _entityRegistry = null!;

    // ICoreService implementation
    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<SceneEntityRegistry>();
        serviceCollection.AddSingleton<ISceneEntityRegistry>(sp => sp.GetRequiredService<SceneEntityRegistry>());
        serviceCollection.AddSingleton<ISceneManager>(this);
        serviceCollection.AddSingleton<ISceneRead>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        _services = services;
        _replicaManager = services.GetRequiredService<IReplicaManager>();
        _eventManager = services.GetRequiredService<IEventManager>();
        _logger = services.GetRequiredService<ILogger>();
        _entityRegistry = services.GetRequiredService<ISceneEntityRegistry>();

        // Direct callback registration (no EventManager queue needed!)
        if (_replicaManager is ReplicaManager replicaManagerImpl)
        {
            replicaManagerImpl.OnReplicaReceivedCallback = OnReplicaReceived;
            replicaManagerImpl.OnReplicaDeletedCallback = OnReplicaDeleted;
            _logger.LogDebug("SceneManager registered direct callbacks with ReplicaManager");
        }
        else
        {
            // Fallback to EventManager if ReplicaManager doesn't support callbacks
            _eventManager.Subscribe<ReplicaDeletedEvent>("SceneManager", OnReplicaDeleted);
            _eventManager.Subscribe<ReplicaReceivedEvent>("SceneManager", OnReplicaReceived);
            _logger.LogDebug("SceneManager subscribed to EventManager (fallback)");
        }
        
        _logger.LogDebug("SceneManager initialized with dependencies from DI");
    }

    public void Start(CancellationToken cancellationToken)
    {
        // SceneManager has no background tasks
    }

    public void Stop()
    {
        // Unregister callbacks on shutdown
        if (_replicaManager is ReplicaManager replicaManagerImpl)
        {
            replicaManagerImpl.OnReplicaReceivedCallback = null;
            replicaManagerImpl.OnReplicaDeletedCallback = null;
            _logger.LogDebug("SceneManager unregistered callbacks from ReplicaManager");
        }
        
        // Also unsubscribe from EventManager (for fallback compatibility)
        _eventManager?.Unsubscribe<ReplicaDeletedEvent>("SceneManager");
        _eventManager?.Unsubscribe<ReplicaReceivedEvent>("SceneManager");
    }

    /// <summary>
    /// Event handler for replica deletion events.
    /// This breaks the circular dependency between SceneManager and ReplicaManager.
    /// </summary>
    private void OnReplicaDeleted(ReplicaDeletedEvent evt)
    {
        _logger.LogDebug($"[SceneManager] Received ReplicaDeletedEvent for {evt.ReplicaType} '{evt.ReplicaId}'");
        using (SceneMutationScope.Begin())
        {
            RemoveNodeLocal(evt.ReplicaId);
            RemoveEntity(evt.ReplicaId);
        }
    }

    /// <summary>
    /// Event handler for receiving replicas from the network.
    /// Creates scene objects when remote peers send them.
    /// </summary>
    private void OnReplicaReceived(ReplicaReceivedEvent evt)
    {
        try
        {
            using (SceneMutationScope.Begin())
                OnReplicaReceivedCore(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[SceneManager] Error processing ReplicaReceivedEvent for '{evt.ReplicaId}': {ex.Message}");
            _logger.LogError($"[SceneManager] Exception details: {ex}");
        }
    }

    private void OnReplicaReceivedCore(ReplicaReceivedEvent evt)
    {
        _logger.LogDebug($"🔥 [SceneManager] OnReplicaReceived CALLED for '{evt.ReplicaId}' of type '{evt.TypeName}'");
        _logger.LogDebug($"[SceneManager] Received ReplicaReceivedEvent for '{evt.ReplicaId}' of type '{evt.TypeName}'");

        var type = Type.GetType(evt.TypeName);
        if (type == null)
        {
            _logger.LogWarning($"[SceneManager] Unknown type '{evt.TypeName}' - ignoring");
            return;
        }

        var tempInstance = Activator.CreateInstance(type) as Replica;
        if (tempInstance == null)
        {
            _logger.LogWarning($"[SceneManager] Failed to create instance of '{evt.TypeName}'");
            return;
        }

        _logger.LogDebug($"[SceneManager] Deserializing instance for '{evt.ReplicaId}'...");
        tempInstance.Deserialize(evt.Payload);

        switch (tempInstance)
        {
            case Entity entityRemote:
                AttachRemoteEntity(entityRemote, evt.OwnerId, evt.SenderId);
                break;
            case Node tempNode:
            {
                var node = CreateNode(tempNode.Id, ownerId: evt.OwnerId);
                (node as Replica)?.Deserialize(evt.Payload);
                node.IsLocal = false;
                _logger.LogDebug($"[SceneManager] Created remote Node '{node.Id}' from peer {evt.SenderId}");
                break;
            }
            default:
                _logger.LogWarning($"[SceneManager] Unhandled replica type '{evt.TypeName}'");
                break;
        }
    }

    /// <summary>
    /// Registers a network-deserialized entity once (no second Deserialize).
    /// </summary>
    private void AttachRemoteEntity(Entity entity, string ownerId, string senderId)
    {
        SceneMutationScope.ThrowIfInactive();
        entity.IsLocal = false;
        entity.OwnerId = ownerId;
        if (entity is Base b)
            b.SceneManager = this;

        var key = entity.Id;
        if (_entities.ContainsKey(key))
        {
            _logger.LogWarning($"[SceneManager] Remote entity '{key}' already exists — skip");
            return;
        }

        _entities[key] = entity;
        _replicaManager.Register((IReplica)entity);
        _logger.LogDebug($"[SceneManager] Attached remote entity {entity.GetType().Name} '{key}' from peer {senderId}");
    }

    // ISceneManager implementation
    public INode CreateNode(string name, string? ownerId = null)
    {
        SceneMutationScope.ThrowIfInactive();
        if (string.IsNullOrEmpty(ownerId))
        {
            ownerId = _replicaManager.GetLocalPeerId();
        }
        
        Node node;
        
        // CRITICAL: Lock ensures atomic path generation + registration
        // Prevents race condition where two threads get the same path
        lock (_pathGenerationLock)
        {
            node = new Node
            {
                Id = name,  // Use name as ID (can be UUID, path, or human-readable name)
                IsLocal = true,  // Will be overridden by ReplicaManager if this is a remote replica
                OwnerId = ownerId  // Set ownership BEFORE registration
            };
            
            // Set UniquePath after construction
            node.SetUniquePathInternal(GenerateUniquePathUnsafe(name));

            // Register immediately while still holding the lock
            // This reserves the path atomically
            RegisterObjectUnsafe(node);
        }
        
        _logger.LogInfo($"Created Node: {name} ({node.Id}) at path '{node.UniquePath}' - Owner: {ownerId}");
        
        return node;
    }

    public INode? GetNode(string nodeId)
    {
        _nodes.TryGetValue(nodeId, out var node);
        return node;
    }

    public INode? GetNodeByPath(string uniquePath)
    {
        _nodesByPath.TryGetValue(uniquePath, out var node);
        return node;
    }

    public ReplicaLock<INode> LockNode(string nodeId, int timeoutMs = 5000)
    {
        var node = GetNode(nodeId);
        return new ReplicaLock<INode>(node, timeoutMs);
    }

    public ReplicaLock<INode> LockNodeByPath(string uniquePath, int timeoutMs = 5000)
    {
        var node = GetNodeByPath(uniquePath);
        return new ReplicaLock<INode>(node, timeoutMs);
    }

    public void RemoveNode(string nodeId)
    {
        SceneMutationScope.ThrowIfInactive();
        if (_nodes.TryRemove(nodeId, out var node))
        {
            _nodesByPath.TryRemove(node.UniquePath, out _);
            _replicaManager.Unregister(nodeId);
            _logger.LogInfo($"Removed node: {node.Id} ({nodeId})");
        }
    }

    /// <summary>
    /// Remove a node from the scene without calling ReplicaManager.Unregister.
    /// This is used when ReplicaManager itself is handling the deletion (e.g., from network).
    /// </summary>
    internal void RemoveNodeLocal(string nodeId)
    {
        SceneMutationScope.ThrowIfInactive();
        if (_nodes.TryRemove(nodeId, out var node))
        {
            _nodesByPath.TryRemove(node.UniquePath, out _);
            _logger.LogInfo($"Removed node (local): {node.Id} ({nodeId})");
        }
    }

    public IEnumerable<INode> GetAllNodes()
    {
        return _nodes.Values;
    }

    /// <summary>
    /// Registers a scene object (Node) in both dictionaries and with ReplicaManager.
    /// UNSAFE: Must be called within _pathGenerationLock to prevent path collisions.
    /// </summary>
    private void RegisterObjectUnsafe(INode node)
    {
        _nodes[node.Id] = node;
        _nodesByPath[node.UniquePath] = node;
        _replicaManager.Register((IReplica)node);
    }

    /// <summary>
    /// Generates a unique path for a node by appending a counter if needed.
    /// UNSAFE: Must be called within _pathGenerationLock to prevent race conditions.
    /// </summary>
    private string GenerateUniquePathUnsafe(string name)
    {
        // Simple path generation - can be enhanced to support hierarchies
        var basePath = $"/{name}";
        var uniquePath = basePath;
        var counter = 1;

        while (_nodesByPath.ContainsKey(uniquePath))
        {
            uniquePath = $"{basePath}_{counter++}";
        }

        return uniquePath;
    }
    
    // ===========================================================================================
    // GENERIC ENTITY API (enum-based factory pattern - clean & extensible)
    // ===========================================================================================
    
    /// <inheritdoc />
    public IEntity CreateRegisteredEntity(string typeId, string name, string? ownerId = null)
    {
        SceneMutationScope.ThrowIfInactive();
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        if (!_entityRegistry.TryCreate(typeId, _services, name, ownerId, out var entity) || entity == null)
            throw new ArgumentException($"Unknown scene entity type id: '{typeId}'", nameof(typeId));

        FinalizeLocalEntity(entity, name, ownerId);
        _logger.LogDebug($"[SceneManager] Created registered entity: {name} (typeId: {typeId})");
        return entity;
    }

    /// <summary>
    /// Owner, local flag, scene wiring, entity dictionary, replica registration.
    /// </summary>
    private void FinalizeLocalEntity(IEntity entity, string dictionaryKey, string? ownerId)
    {
        if (_entities.ContainsKey(dictionaryKey))
            throw new ArgumentException($"Entity '{dictionaryKey}' already exists!");

        if (string.IsNullOrEmpty(ownerId))
            ownerId = _replicaManager.GetLocalPeerId();

        if (entity is IReplica replica)
        {
            replica.OwnerId = ownerId;
            replica.IsLocal = true;
        }

        if (entity is Base baseObj)
            baseObj.SceneManager = this;

        _entities[dictionaryKey] = entity;
        _replicaManager.Register((IReplica)entity);
    }
    
    /// <summary>
    /// Get entity by name (returns interface, cast to concrete type)
    /// </summary>
    public IEntity? GetEntity(string name)
    {
        return _entities.TryGetValue(name, out var entity) ? entity : null;
    }
    
    /// <summary>
    /// Get entity by name and cast to specific type
    /// </summary>
    public T? GetEntity<T>(string name) where T : class, IEntity
    {
        return GetEntity(name) as T;
    }
    
    /// <summary>
    /// Get all entities of a specific type
    /// </summary>
    public IEnumerable<T> GetEntities<T>() where T : class, IEntity
    {
        return _entities.Values.OfType<T>();
    }

    public IEnumerable<IEntity> GetAllEntities() => _entities.Values;
    
    /// <summary>
    /// Remove entity by name
    /// </summary>
    public bool RemoveEntity(string name)
    {
        SceneMutationScope.ThrowIfInactive();
        if (_entities.Remove(name, out var entity))
        {
            _replicaManager.Unregister(name);
            _logger.LogDebug($"[SceneManager] Removed entity: {name} (type: {entity.GetType().Name})");
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Check if entity exists
    /// </summary>
    public bool HasEntity(string name)
    {
        return _entities.ContainsKey(name);
    }
}
