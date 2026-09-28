using Ape.Core.Runtime.Service;
using Ape.Core.Event;
using Ape.Core.Event.Models;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Ape.Core.Replication.Services;

/// <summary>
/// Manages the lifecycle and synchronization of Replica instances.
/// Handles serialization, network transport, and property change notifications.
/// Also implements ICoreService for direct registration in the DI container.
/// </summary>
public class ReplicaManager : IReplicaManager, ICoreService
{
    public string ServiceId => "core-replica-manager";
    public string Name => "Replica Manager";

    private readonly ConcurrentDictionary<string, IReplica> _replicas = new();
    private INetworkManager _networkManager = null!;  // Set in Initialize()
    private IEventManager _eventManager = null!;      // Set in Initialize()
    private ILogger _logger = null!;                   // Set in Initialize()
    private IReplicaAccessControlService? _accessControl;  // Optional; null = broadcast to all
    private readonly object _tickLock = new();

    // Direct callbacks for replica events (bypasses EventManager queue system)
    public Action<ReplicaReceivedEvent>? OnReplicaReceivedCallback { get; set; }
    public Action<ReplicaDeletedEvent>? OnReplicaDeletedCallback { get; set; }

    // ICoreService implementation
    public void Register(IServiceCollection serviceCollection)
    {
        // Register this instance as the IReplicaManager singleton
        serviceCollection.AddSingleton<IReplicaManager>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        // Get dependencies from DI
        _networkManager = services.GetRequiredService<INetworkManager>();
        _eventManager = services.GetRequiredService<IEventManager>();
        _logger = services.GetRequiredService<ILogger>();
        _accessControl = services.GetService<IReplicaAccessControlService>();

        _logger.LogDebug($"[ReplicaManager] Initialize called (NetworkManager HashCode: {_networkManager.GetHashCode()})");
        _logger.LogDebug("[ReplicaManager] Subscribing to NetworkManager events");

        // Unsubscribe first to prevent duplicate registrations if Initialize is called multiple times
        // NOTE: This only works if networkManager is the SAME instance!
        _networkManager.OnDataReceived -= OnNetworkData;
        _networkManager.OnPeerDisconnected -= OnPeerDisconnected;
        _networkManager.OnPeerConnected -= OnPeerConnected;

        // Now subscribe
        _networkManager.OnDataReceived += OnNetworkData;
        _networkManager.OnPeerDisconnected += OnPeerDisconnected;
        _networkManager.OnPeerConnected += OnPeerConnected;

        _logger.LogDebug("[ReplicaManager] Event subscriptions complete");
    }

    public void Start(CancellationToken cancellationToken)
    {
        // ReplicaManager has no background tasks (Tick() called by main loop)
    }

    public void Stop()
    {
        // Unsubscribe from events on shutdown
        if (_networkManager != null)
        {
            _networkManager.OnDataReceived -= OnNetworkData;
            _networkManager.OnPeerDisconnected -= OnPeerDisconnected;
            _networkManager.OnPeerConnected -= OnPeerConnected;
        }
    }

    private readonly HashSet<string> _syncedPeers = new();

    private void OnPeerConnected(string peerId)
    {
        _accessControl?.RegisterPeer(peerId);
        _accessControl?.ApplyImplicitSubscribe(peerId);

        // Prevent duplicate initial sync if OnPeerConnected fires multiple times
        // CRITICAL: Must add to HashSet AND call SendInitialSync inside the same lock!
        lock (_syncedPeers)
        {
            if (_syncedPeers.Contains(peerId))
            {
                _logger.LogWarning($"[ReplicaManager] Peer {peerId} already synced - ignoring duplicate OnPeerConnected event");
                _logger.LogWarning($"[ReplicaManager] Call stack:\n{Environment.StackTrace}");
                return;
            }
            _syncedPeers.Add(peerId);

            _logger.LogInfo($"[ReplicaManager] Peer {peerId} connected - sending initial sync (call #{(_syncedPeers.Count)})");
            SendInitialSync(peerId);
        }
    }

    private void OnPeerDisconnected(string peerId)
    {
        _logger.LogInfo($"[ReplicaManager] Peer {peerId} disconnected");
        _accessControl?.UnregisterPeer(peerId);

        // Remove from synced peers list
        lock (_syncedPeers)
        {
            _syncedPeers.Remove(peerId);
        }

        // NetworkManager already handles removing the peer from its connection list
        // No need to clean up replicas - they remain for reconnection scenarios
    }

    public void Register(IReplica replica)
    {
        if (_replicas.TryAdd(replica.Id, replica))
        {
            // Set ownership if not already set (locally created replicas)
            if (string.IsNullOrEmpty(replica.OwnerId))
            {
                replica.OwnerId = _networkManager.LocalPeerId;
                _logger.LogInfo($"Registered LOCAL replica: {replica.Id} ({replica.GetType().Name}) - Owner: {replica.OwnerId}");
            }
            else
            {
                _logger.LogInfo($"Registered REMOTE replica: {replica.Id} ({replica.GetType().Name}) - Owner: {replica.OwnerId}");
            }

            // Inject EventManager dependency into replica
            replica.EventManager = _eventManager;

            // Snapshot initial state for change detection
            replica.SnapshotProperties();

            // Publish registration event
            _eventManager.Publish(new ReplicaRegisteredEvent
            {
                ReplicaId = replica.Id,
                ReplicaType = replica.GetType().Name,
                IsLocal = replica.IsLocal  // Keep for now, will remove later
            });

            // Only broadcast if we own this replica
            bool weOwnThis = replica.OwnerId == _networkManager.LocalPeerId;
            if (weOwnThis)
            {
                try
                {
                    var packet = ReplicaPacketBuilder.BuildPacket(ReplicaPacketType.Create, replica);
                    SendToRecipients(replica.UniquePath, packet);
                    _logger.LogDebug($"Sent Create packet for replica {replica.Id} (we own it)");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error sending Create packet for replica {replica.Id}", ex);
                }
            }
        }
        else
        {
            _logger.LogWarning($"Replica {replica.Id} already registered");
        }
    }

    public void Unregister(string replicaId)
    {
        if (_replicas.TryRemove(replicaId, out var replica))
        {
            _logger.LogInfo($"Unregistered replica: {replicaId}");

            _eventManager.Publish(new ReplicaUnregisteredEvent
            {
                ReplicaId = replicaId
            });

            // Send Delete packet to subscribed peers (or broadcast if no access control)
            try
            {
                var packet = ReplicaPacketBuilder.BuildDeletePacket(replicaId);
                SendToRecipients(replica.UniquePath, packet);
                _logger.LogDebug($"Sent Delete packet for replica {replicaId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending Delete packet for replica {replicaId}", ex);
            }
        }
    }

    public IReplica? GetReplica(string replicaId)
    {
        _replicas.TryGetValue(replicaId, out var replica);
        return replica;
    }

    public void Tick()
    {
        lock (_tickLock)
        {
            // Serialize and send only changed replicas that we own
            var localPeerId = _networkManager.LocalPeerId;
            foreach (var replica in _replicas.Values.Where(r => r.OwnerId == localPeerId))
            {
                // Skip if no changes since last snapshot
                if (!replica.HasChanges())
                    continue;

                try
                {
                    var packet = ReplicaPacketBuilder.BuildPacket(ReplicaPacketType.Update, replica);
                    SendToRecipients(replica.UniquePath, packet);

                    // Update snapshot after successful broadcast
                    replica.SnapshotProperties();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error serializing replica {replica.Id}", ex);
                }
            }
        }
    }

    /// <summary>
    /// Send to recipients based on access control. If no access control or GetSubscribedPeers returns null, broadcast.
    /// </summary>
    private void SendToRecipients(string replicaPath, byte[] packet)
    {
        var recipients = _accessControl?.GetSubscribedPeers(replicaPath);
        if (recipients == null)
        {
            _networkManager.Broadcast(packet, reliable: true);
            return;
        }
        foreach (var peerId in recipients)
        {
            _networkManager.Send(peerId, packet, reliable: true);
        }
    }

    /// <summary>
    /// Send initial sync to a newly connected peer.
    /// Sends local replicas the peer is subscribed to. With no access control, sends all owned replicas.
    /// </summary>
    private void SendInitialSync(string peerId)
    {
        var syncCount = 0;
        var localPeerId = _networkManager.LocalPeerId;

        foreach (var replica in _replicas.Values.Where(r => r.OwnerId == localPeerId))
        {
            var recipients = _accessControl?.GetSubscribedPeers(replica.UniquePath);
            if (recipients != null && !recipients.Contains(peerId))
                continue;

            try
            {
                var packet = ReplicaPacketBuilder.BuildPacket(ReplicaPacketType.FullSync, replica);
                _networkManager.Send(peerId, packet, reliable: true);
                syncCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending initial sync for replica {replica.Id} to peer {peerId}", ex);
            }
        }

        _logger.LogInfo($"[ReplicaManager] Sent initial sync to peer {peerId}: {syncCount} replicas (owned by {localPeerId})");
    }

    public void OnNetworkData(string peerId, byte[] data)
    {
        try
        {
            if (ReplicaPacketBuilder.IsPluginHandledPacket(data))
                return;

            // Parse packet type
            if (!ReplicaPacketBuilder.TryParsePacketType(data, out var packetType))
            {
                _logger.LogWarning($"Received packet with invalid type from peer {peerId}");
                return;
            }

            // Handle Delete packet separately (no payload)
            if (packetType == ReplicaPacketType.Delete)
            {
                if (!ReplicaPacketBuilder.TryParseId(data, out var replicaId))
                {
                    _logger.LogWarning($"Received invalid Delete packet from peer {peerId}");
                    return;
                }

                HandleDeletePacket(replicaId);
                return;
            }

            // Handle Subscribe/Unsubscribe control plane
            if (packetType == ReplicaPacketType.Subscribe || packetType == ReplicaPacketType.Unsubscribe)
            {
                HandleControlPacket(peerId, packetType, data);
                return;
            }

            // Handle Create/Update/FullSync packets (all have payload)
            if (!ReplicaPacketBuilder.TryParsePacket(data, out var parsedReplicaId, out var ownerId, out var typeName, out var payload))
            {
                _logger.LogWarning($"Received invalid packet from peer {peerId}: parsing failed");
                return;
            }

            if (_replicas.TryGetValue(parsedReplicaId, out var replica))
            {
                // Update existing replica
                _logger.LogDebug($"Received {packetType} packet for existing replica '{parsedReplicaId}' - deserializing...");
                using (SceneMutationScope.Begin())
                    replica.Deserialize(payload);
                _logger.LogInfo($"✅ Updated replica: {replica.GetType().Name} (ID: {parsedReplicaId}, Owner: {ownerId})");

                // PropertyChangedEvent is now automatically generated by individual Replica classes
                // via their property setters (e.g., Device.DeviceData setter) - no manual event generation needed!
            }
            else
            {
                // Create new replica from network
                _logger.LogDebug($"Replica '{parsedReplicaId}' NOT FOUND in _replicas dictionary (count: {_replicas.Count})");
                HandleCreatePacket(parsedReplicaId, ownerId, typeName, payload, peerId, packetType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error processing network data from peer {peerId}", ex);
        }
    }

    private void HandleControlPacket(string peerId, ReplicaPacketType packetType, byte[] data)
    {
        if (_accessControl == null)
        {
            _logger.LogDebug($"[ReplicaManager] Ignoring {packetType} - no access control service");
            return;
        }

        if (!ReplicaPacketBuilder.TryParsePathPattern(data, out var pathPattern))
        {
            _logger.LogWarning($"Received invalid {packetType} packet from peer {peerId}");
            return;
        }

        if (packetType == ReplicaPacketType.Subscribe)
        {
            if (_accessControl.TrySubscribe(peerId, pathPattern, out var denyReason, "read"))
                _logger.LogInfo($"[ReplicaManager] Peer {peerId} subscribed to {pathPattern}");
            else
                _logger.LogWarning($"[ReplicaManager] Peer {peerId} subscribe denied: {denyReason}");
        }
        else
        {
            _accessControl.Unsubscribe(peerId, pathPattern);
            _logger.LogInfo($"[ReplicaManager] Peer {peerId} unsubscribed from {pathPattern}");
        }
    }

    private void HandleDeletePacket(string replicaId)
    {
        if (_replicas.TryRemove(replicaId, out var replica))
        {
            var replicaType = replica.GetType().Name;
            var ownerId = replica.OwnerId;

            _logger.LogInfo($"Remote replica deleted: {replicaId} ({replicaType})");

            var deleteEvent = new ReplicaDeletedEvent
            {
                ReplicaId = replicaId,
                ReplicaType = replicaType,
                OwnerId = ownerId
            };

            // Direct callback (immediate, no queue!)
            OnReplicaDeletedCallback?.Invoke(deleteEvent);

            // Also publish to EventManager for plugins that still use it
            _eventManager.Publish(deleteEvent);

            // Also publish the legacy unregistered event for backward compatibility
            _eventManager.Publish(new ReplicaUnregisteredEvent
            {
                ReplicaId = replicaId
            });
        }
        else
        {
            _logger.LogDebug($"Received Delete packet for unknown replica {replicaId}");
        }
    }

    private void HandleCreatePacket(string replicaId, string ownerId, string typeName, byte[] payload, string peerId, ReplicaPacketType packetType)
    {
        _logger.LogDebug($"Received {packetType} packet for replica '{replicaId}' of type '{typeName}' from peer {peerId}");

        var replicaEvent = new ReplicaReceivedEvent
        {
            ReplicaId = replicaId,
            OwnerId = ownerId,
            TypeName = typeName,
            Payload = payload,
            SenderId = peerId
        };

        // Direct callback (immediate, no queue!)
        OnReplicaReceivedCallback?.Invoke(replicaEvent);

        // Also publish to EventManager for plugins that still use it
        _eventManager.Publish(replicaEvent);
    }

    public string GetLocalPeerId()
    {
        return _networkManager.LocalPeerId;
    }

    public void Subscribe(string pathPattern)
    {
        var packet = ReplicaPacketBuilder.BuildControlPacket(ReplicaPacketType.Subscribe, pathPattern);
        _networkManager.Broadcast(packet, reliable: true);
        _logger.LogDebug($"[ReplicaManager] Sent Subscribe for {pathPattern}");
    }

    public void Unsubscribe(string pathPattern)
    {
        var packet = ReplicaPacketBuilder.BuildControlPacket(ReplicaPacketType.Unsubscribe, pathPattern);
        _networkManager.Broadcast(packet, reliable: true);
        _logger.LogDebug($"[ReplicaManager] Sent Unsubscribe for {pathPattern}");
    }
}
