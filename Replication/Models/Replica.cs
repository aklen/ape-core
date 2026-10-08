using MessagePack;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ape.Core.Network.FileTransfer;
using Ape.Core.Event;
using Ape.Core.Scene;

namespace Ape.Core.Replication;

/// <summary>
/// Base class for all replicable objects in ApeCore.
/// Provides automatic serialization, property change detection, and network synchronization.
/// Property changes automatically trigger PropertyChangedEvent via the EventManager.
/// Events are ONLY fired when properties change through setters, ensuring consistency.
/// 
/// Inheritance hierarchy:
/// - Replica → Base → Node (transform)
/// - Replica → Base → Entity (functional scene objects; concretes in Ape.Module.*)
/// </summary>
[MessagePackObject]
[Union(0, typeof(Scene.Models.Node))]
public abstract class Replica : IReplica
{
    /// <summary>
    /// Unique string-based identifier for this replica.
    /// Can be UUID, hierarchical path, or custom name.
    /// Set during construction or by SceneManager.
    /// </summary>
    [Key(0)]
#pragma warning disable MsgPack017 // Serialize() always writes key 0; the Guid initializer is for local construction.
    public string Id { get; init; } = Guid.NewGuid().ToString();
#pragma warning restore MsgPack017
    
    [IgnoreMember]
    public bool IsLocal { get; set; } = true;
    
    /// <summary>
    /// Owner peer ID for this replica. Identifies which peer owns this object.
    /// Empty string = no owner (not yet assigned). Set by ReplicaManager during Register().
    /// Used for ownership tracking and broadcast filtering.
    /// </summary>
    [IgnoreMember]
    public string OwnerId { get; set; } = string.Empty;
    
    /// <summary>
    /// Unique hierarchical path for this replica in the scene hierarchy.
    /// Auto-generated based on parent-child relationships via SetParentNode().
    /// Examples: "/Root", "/Root/CameraNode", "/Root/Lights/MainLight"
    /// </summary>
    [IgnoreMember]
    public string UniquePath { get; protected set; } = string.Empty;
    
    /// <summary>
    /// Internal method for setting UniquePath during object construction.
    /// Should only be used by SceneManager or Base class hierarchy management.
    /// </summary>
    internal void SetUniquePathInternal(string uniquePath)
    {
        UniquePath = uniquePath;
    }
    
    /// <summary>
    /// Synchronization object for thread-safe operations.
    /// Use lock(replica.SyncRoot) for manual locking.
    /// For RAII-style locking, use ReplicaLock (see ISceneManager.LockNode).
    /// </summary>
    [IgnoreMember]
    public object SyncRoot { get; } = new object();
    
    [IgnoreMember]
    private readonly ConcurrentDictionary<string, object?> _previousValues = new();
    
    [IgnoreMember]
    private IEventManager? _eventManager;
    
    [IgnoreMember]
    private ILargeFileTransferService? _largeFileTransferService;
    
    [IgnoreMember]
    private bool _suppressEvents = false;
    
    /// <summary>
    /// Internal setter for EventManager dependency.
    /// Called by ReplicaManager during registration.
    /// </summary>
    [IgnoreMember]
    public IEventManager? EventManager
    {
        get => _eventManager;
        set => _eventManager = value;
    }
    
    /// <summary>
    /// Internal setter for LargeFileTransferService dependency.
    /// Called by ReplicaManager during registration.
    /// </summary>
    [IgnoreMember]
    public ILargeFileTransferService? LargeFileTransferService
    {
        get => _largeFileTransferService;
        set => _largeFileTransferService = value;
    }
    
    /// <summary>
    /// Serialize this replica to bytes using MessagePack.
    /// Uses the concrete runtime type for proper polymorphic serialization.
    /// </summary>
    public virtual byte[] Serialize()
    {
        // Serialize using the actual runtime type, not the base Replica type
        return MessagePackSerializer.Serialize(GetType(), this, GetMessagePackOptions());
    }
    
    /// <summary>
    /// Deserialize data into this replica and detect property changes.
    /// Fires property changed events for any modified properties via setters.
    /// This ensures events are triggered consistently whether changes come from
    /// local code or network updates.
    /// </summary>
    public virtual void Deserialize(ReadOnlyMemory<byte> data)
    {
        SceneMutationScope.ThrowIfInactive();
        var type = GetType();
        var deserialized = MessagePackSerializer.Deserialize(type, data, GetMessagePackOptions());
        
        if (deserialized == null)
            return;
        
        // Copy properties - setters will automatically trigger OnPropertyChanged
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<KeyAttribute>() != null);
        
        foreach (var prop in properties)
        {
            var newValue = prop.GetValue(deserialized);
            var oldValue = prop.GetValue(this);
            
            if (!Equals(oldValue, newValue))
            {
                // Set the property value - setter will call SetProperty() which calls OnPropertyChanged()
                // No need to manually call OnPropertyChanged() - it creates duplicates!
                prop.SetValue(this, newValue);
            }
        }
    }
    
    /// <summary>
    /// Called automatically when a property changes through its setter.
    /// Publishes a notification-only PropertyChangedEvent to the EventManager.
    /// The event does NOT contain property values - plugins must read from the replica.
    /// Override this to add custom behavior on property changes.
    /// </summary>
    protected virtual void OnPropertyChanged(string propertyName)
    {
        // Don't fire events if suppressed (e.g., during initial deserialization)
        if (_suppressEvents)
            return;
        
        // Check if property has [FileChunkTransfer] attribute
        var property = GetType().GetProperty(propertyName);
        var fileChunkAttr = property?.GetCustomAttribute<FileChunkTransferAttribute>();
        
        if (fileChunkAttr != null && property != null)
        {
            // Get the file path from the property value
            var filePath = property.GetValue(this) as string;
            
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                // Trigger large file transfer asynchronously
                _ = TriggerLargeFileTransferAsync(filePath, fileChunkAttr);
            }
        }
        
        // Publish notification-only event to EventManager if available
        _eventManager?.Publish(new PropertyChangedEvent(
            Id,
            UniquePath,
            propertyName
        ));
    }
    
    /// <summary>
    /// Trigger large file transfer for a property marked with [FileChunkTransfer].
    /// Called automatically when the property changes.
    /// </summary>
    private async Task TriggerLargeFileTransferAsync(string filePath, FileChunkTransferAttribute attr)
    {
        if (_largeFileTransferService == null)
        {
            // Service not available, skip large file transfer
            return;
        }
        
        try
        {
            // Upload file using chunk-based transfer
            var fileId = await _largeFileTransferService.UploadFileAsync(
                filePath, 
                attr.ChunkSize, 
                CancellationToken.None
            );
            
            // Optional: Store fileId in a CAS reference (e.g., "cas:abc123...")
            // This would require updating the property value to the CAS reference
            // For Phase 1, we just upload the chunks
        }
        catch (Exception ex)
        {
            // Log error (if logger available)
            // For now, silently fail
            Console.WriteLine($"[Replica] Large file transfer failed for {filePath}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Helper method for property setters to track changes and fire events.
    /// Call this from your property setters with the backing field.
    /// Fires a notification-only event (no old/new values).
    /// </summary>
    /// <typeparam name="T">Property type</typeparam>
    /// <param name="field">Reference to the backing field</param>
    /// <param name="value">New value</param>
    /// <param name="propertyName">Property name (auto-filled by compiler)</param>
    /// <returns>True if the value changed</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        SceneMutationScope.ThrowIfInactive();
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        
        field = value;
        
        OnPropertyChanged(propertyName);
        return true;
    }
    
    /// <summary>
    /// Suppress event firing temporarily (e.g., during bulk updates).
    /// </summary>
    protected IDisposable SuppressEvents()
    {
        return new EventSuppressor(this);
    }
    
    private class EventSuppressor : IDisposable
    {
        private readonly Replica _replica;
        
        public EventSuppressor(Replica replica)
        {
            _replica = replica;
            _replica._suppressEvents = true;
        }
        
        public void Dispose()
        {
            _replica._suppressEvents = false;
        }
    }
    
    /// <summary>
    /// Snapshot current property values for change detection.
    /// Call this before making modifications to track changes.
    /// </summary>
    public void SnapshotProperties()
    {
        var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetCustomAttribute<KeyAttribute>() != null);
        
        foreach (var prop in properties)
        {
            _previousValues[prop.Name] = prop.GetValue(this);
        }
    }
    
    /// <summary>
    /// Manually trigger property change notification for a specific property.
    /// Use this when modifying dictionary/collection contents that don't automatically trigger property setters.
    /// Example: After updating DeviceData dictionary, call NotifyPropertyChanged("DeviceData") to sync changes.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed</param>
    public void NotifyPropertyChanged(string propertyName)
    {
        OnPropertyChanged(propertyName);
    }
    
    /// <summary>
    /// Check if any properties have changed since the last snapshot.
    /// Returns true if any serializable property differs from its snapshotted value.
    /// </summary>
    public bool HasChanges()
    {
        var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetCustomAttribute<KeyAttribute>() != null);
        
        foreach (var prop in properties)
        {
            if (!_previousValues.TryGetValue(prop.Name, out var previousValue))
                return true; // Property not snapshotted yet
            
            var currentValue = prop.GetValue(this);
            if (!Equals(previousValue, currentValue))
                return true; // Property changed
        }
        
        return false; // No changes detected
    }
    
    /// <summary>
    /// Get MessagePack serialization options with custom formatters for ApeCore types.
    /// Override to customize serialization behavior.
    /// </summary>
    protected virtual MessagePackSerializerOptions GetMessagePackOptions()
    {
        return MessagePackSerializerOptions.Standard
            .WithResolver(ReplicaResolver.Instance)
            .WithCompression(MessagePackCompression.Lz4Block);
    }
}
