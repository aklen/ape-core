using Ape.Core.Event;
using MessagePack;

namespace Ape.Core.Replication;

/// <summary>
/// Event fired when a replica property changes.
/// Use ReplicaId or UniquePath to retrieve the replica object.
/// </summary>
[MessagePackObject]
public class PropertyChangedEvent : IEvent
{
    [Key(0)]
    public string EventId { get; init; }

    [Key(1)]
    public DateTime Timestamp { get; init; }

    [Key(2)]
    public string ReplicaId { get; init; }

    [Key(3)]
    public string UniquePath { get; init; }

    [Key(4)]
    public string PropertyName { get; init; }

    public PropertyChangedEvent()
    {
        EventId = string.Empty;
        Timestamp = DateTime.UtcNow;
        ReplicaId = string.Empty;
        UniquePath = string.Empty;
        PropertyName = string.Empty;
    }

    public PropertyChangedEvent(string replicaId, string uniquePath, string propertyName)
    {
        EventId = Guid.NewGuid().ToString();
        Timestamp = DateTime.UtcNow;
        ReplicaId = replicaId;
        UniquePath = uniquePath;
        PropertyName = propertyName;
    }
}
