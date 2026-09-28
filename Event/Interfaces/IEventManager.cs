namespace Ape.Core.Event;

/// <summary>
/// Thread-safe event manager for plugin communication.
/// Uses concurrent queues to deliver events to plugin-specific handlers.
/// Callbacks execute on plugin threads, not the core thread.
/// </summary>
public interface IEventManager
{
    /// <summary>
    /// Subscribe to events of type T for a specific plugin.
    /// </summary>
    void Subscribe<T>(string pluginId, Action<T> handler) where T : IEvent;

    /// <summary>
    /// Unsubscribe from events of type T for a specific plugin.
    /// </summary>
    void Unsubscribe<T>(string pluginId) where T : IEvent;

    /// <summary>
    /// Publish an event to all subscribers.
    /// Events are queued and delivered asynchronously.
    /// </summary>
    void Publish(IEvent evt);

    /// <summary>
    /// Drain and process all pending events for a specific plugin.
    /// Should be called from the plugin's thread loop.
    /// </summary>
    void DrainEventsFor(string pluginId);
}
