using Ape.Core.Event;

namespace Ape.Core.Event.Models;

/// <summary>
/// Event fired when a new replica is received from the network.
/// This allows SceneManager to create scene objects without ReplicaManager knowing about it.
/// </summary>
public class ReplicaReceivedEvent : IEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public required string ReplicaId { get; init; }

    public required string OwnerId { get; init; }

    /// <summary>
    /// Type name of the received replica (e.g., "Ape.Core.Scene.Models.Node")
    /// </summary>
    public required string TypeName { get; init; }

    public required byte[] Payload { get; init; }

    public required string SenderId { get; init; }
}
