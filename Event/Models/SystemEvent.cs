using Ape.Core.Event;

namespace Ape.Core.Event.Models;

/// <summary>
/// Represents a system-level event in Ape.Core.
/// Used to communicate the state of core components and network discovery.
/// </summary>
public class SystemEvent : IEvent
{
    public string EventId { get; private set; } = Guid.NewGuid().ToString();

    public DateTime Timestamp { get; private set; } = DateTime.UtcNow;

    public SystemEventType EventType { get; private set; }

    public object? Data { get; private set; }

    public SystemEvent(SystemEventType eventType, object? data = null)
    {
        EventType = eventType;
        Data = data;
    }
}

/// <summary>
/// Enum representing the types of system events.
/// </summary>
public enum SystemEventType
{
    CoreStarted,
    CoreStopped,
    NetworkManagerStarted,
    PluginsLoaded,
    DeviceDiscovered,
    DeviceLost
}
