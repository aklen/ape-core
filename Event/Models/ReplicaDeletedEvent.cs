using Ape.Core.Event;

namespace Ape.Core.Event.Models;

/// <summary>
/// Event fired when a replica is deleted from the network.
/// This allows SceneManager to remove scene objects without circular dependency on ReplicaManager.
/// </summary>
public class ReplicaDeletedEvent : IEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public required string ReplicaId { get; init; }

    public required string ReplicaType { get; init; }

    public string? OwnerId { get; init; }
}
