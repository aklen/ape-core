using Ape.Core.Logging;
using Ape.Core.Runtime.Service;
using Ape.Core.Event;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Ape.Core.Event.Services;

/// <summary>
/// Thread-safe event manager implementation using concurrent queues.
/// Each plugin has its own event queue to process events on its own thread.
/// Implements backpressure: drops events if a plugin's queue exceeds MAX_QUEUE_SIZE.
/// Also implements ICoreService for direct registration in the DI container.
/// </summary>
public class EventManager : IEventManager, ICoreService
{
    public string ServiceId => "core-event-manager";
    public string Name => "Event Manager";

    private const int MAX_QUEUE_SIZE = 10000;

    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, Delegate>> _subscriptions = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(IEvent, Delegate)>> _pluginQueues = new();
    private readonly ConcurrentDictionary<string, int> _queueSizes = new();
    private ILogger? _logger;

    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<IEventManager>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        _logger = services.GetRequiredService<ILogger>();
    }

    public void Start(CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
    }

    public void Subscribe<T>(string pluginId, Action<T> handler) where T : IEvent
    {
        var eventType = typeof(T);

        var handlers = _subscriptions.GetOrAdd(eventType, _ => new ConcurrentDictionary<string, Delegate>());
        handlers[pluginId] = handler;

        _pluginQueues.TryAdd(pluginId, new ConcurrentQueue<(IEvent, Delegate)>());

        _logger?.LogDebug($"Plugin '{pluginId}' subscribed to {eventType.Name}");
    }

    public void Unsubscribe<T>(string pluginId) where T : IEvent
    {
        var eventType = typeof(T);

        if (_subscriptions.TryGetValue(eventType, out var handlers))
        {
            handlers.TryRemove(pluginId, out _);
            _logger?.LogDebug($"Plugin '{pluginId}' unsubscribed from {eventType.Name}");
        }
    }

    public void Publish(IEvent evt)
    {
        var eventType = evt.GetType();

        if (!_subscriptions.TryGetValue(eventType, out var handlers))
            return;

        foreach (var (pluginId, handler) in handlers)
        {
            if (_pluginQueues.TryGetValue(pluginId, out var queue))
            {
                var currentSize = _queueSizes.GetOrAdd(pluginId, 0);

                if (currentSize >= MAX_QUEUE_SIZE)
                {
                    _logger?.LogWarning(
                        $"⚠️ Event queue full for plugin '{pluginId}' ({currentSize} events). " +
                        $"Dropping event: {eventType.Name}. Plugin may be slow or blocked!");
                    continue;
                }

                queue.Enqueue((evt, handler));
                _queueSizes.AddOrUpdate(pluginId, 1, (_, count) => count + 1);
            }
        }
    }

    public void DrainEventsFor(string pluginId)
    {
        if (!_pluginQueues.TryGetValue(pluginId, out var queue))
            return;

        var processedCount = 0;

        while (queue.TryDequeue(out var item))
        {
            var (evt, handler) = item;

            try
            {
                handler.DynamicInvoke(evt);
                processedCount++;

                _queueSizes.AddOrUpdate(pluginId, 0, (_, count) => Math.Max(0, count - 1));
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error processing event {evt.GetType().Name} in plugin '{pluginId}'", ex);

                _queueSizes.AddOrUpdate(pluginId, 0, (_, count) => Math.Max(0, count - 1));
            }
        }

        if (processedCount > 0)
        {
            _logger?.LogDebug($"Plugin '{pluginId}' processed {processedCount} events");
        }
    }
}
