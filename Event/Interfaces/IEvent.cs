namespace Ape.Core.Event;

/// <summary>
/// Base interface for all events in the ApeCore event system.
/// Events are published through the EventManager and delivered to plugin-specific queues.
/// </summary>
public interface IEvent
{
    /// <summary>
    /// Unique identifier for this event instance.
    /// </summary>
    string EventId { get; }

    /// <summary>
    /// Timestamp when the event was created.
    /// </summary>
    DateTime Timestamp { get; }
}
