using Ape.Core.Event;

namespace Ape.Core.Replication;

/// <summary>
/// Event fired when a replica is registered.
/// </summary>
public class ReplicaRegisteredEvent : IEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string ReplicaId { get; init; } = string.Empty;
    public string ReplicaType { get; init; } = string.Empty;
    public bool IsLocal { get; init; }
}
