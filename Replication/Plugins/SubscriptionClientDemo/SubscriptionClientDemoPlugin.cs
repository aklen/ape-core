using System.Numerics;
using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Scene;

namespace Ape.Core.Replication.Plugin.SubscriptionClientDemo;

/// <summary>
/// Client-side subscription demo without explicit Subscribe — relies on implicit subscribe from server config.
/// Logs when <c>/PingNode</c> replicas arrive or change.
/// </summary>
public sealed class SubscriptionClientDemoPlugin : IPlugin
{
    public const string PingNodePath = "/PingNode";

    public string PluginId => "subscription-client-demo";
    public string Name => "Subscription Client Demo";

    private IEventManager? _eventManager;
    private ISceneRead? _sceneRead;
    private INetworkManager? _networkManager;
    private ILogger? _logger;
    private INode? _pingNode;
    private Vector3 _lastPosition;

    public void OnInit(IServiceProvider services)
    {
        _eventManager = services.GetService(typeof(IEventManager)) as IEventManager;
        _sceneRead = services.GetService(typeof(ISceneRead)) as ISceneRead;
        _networkManager = services.GetService(typeof(INetworkManager)) as INetworkManager;
        _logger = services.GetService(typeof(ILogger)) as ILogger;
        _eventManager?.Subscribe<PropertyChangedEvent>(PluginId, OnPropertyChanged);
        _logger?.LogInfo($"{Name} initialized");
    }

    public void OnRun(CancellationToken cancellationToken)
    {
        _logger?.LogInfo($"{Name} thread started");

        if (!string.Equals(_networkManager?.Role, "client", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInfo($"{Name}: non-client role — idle");
            Idle(cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_pingNode == null)
                {
                    _pingNode = _sceneRead?.GetNodeByPath(PingNodePath);
                    _pingNode?.WithLock(n =>
                    {
                        _lastPosition = n.Position;
                        _logger?.LogInfo($"{Name}: received {PingNodePath}, Position={n.Position}");
                    });
                }

                _eventManager?.DrainEventsFor(PluginId);
                Task.Delay(50, cancellationToken).Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{Name} loop error", ex);
            }
        }

        _logger?.LogInfo($"{Name} thread exiting");
    }

    public void OnShutdown()
    {
        _eventManager?.Unsubscribe<PropertyChangedEvent>(PluginId);
        _logger?.LogInfo($"{Name} shutdown");
    }

    private void OnPropertyChanged(PropertyChangedEvent evt)
    {
        if (_pingNode == null || evt.ReplicaId != _pingNode.Id || evt.PropertyName != nameof(INode.Position))
            return;

        _pingNode.WithLock(n =>
        {
            var current = n.Position;
            _logger?.LogInfo($"{Name}: {PingNodePath} Position={current} (Δ={Vector3.Distance(_lastPosition, current):F4})");
            _lastPosition = current;
        });
    }

    private static void Idle(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task.Delay(1000, cancellationToken).Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
