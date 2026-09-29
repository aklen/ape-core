using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Runtime.Plugin;

namespace Ape.Core.Replication.Plugin.SubscriptionDemo;

/// <summary>
/// Client-side replica subscription demo: sends explicit <c>Subscribe("/PingNode")</c> after connect.
/// Pair with <see cref="SubscriptionServerDemo.SubscriptionServerDemoPlugin"/> and subscription samples.
/// </summary>
public sealed class SubscriptionDemoPlugin : IPlugin
{
    public const string PingNodePath = "/PingNode";

    public string PluginId => "subscription-demo";
    public string Name => "Subscription Demo";

    private IReplicaManager? _replicaManager;
    private INetworkManager? _networkManager;
    private ILogger? _logger;

    public void OnInit(IServiceProvider services)
    {
        _replicaManager = services.GetService(typeof(IReplicaManager)) as IReplicaManager;
        _networkManager = services.GetService(typeof(INetworkManager)) as INetworkManager;
        _logger = services.GetService(typeof(ILogger)) as ILogger;
        _logger?.LogInfo($"{Name} initialized");
    }

    public void OnRun(CancellationToken cancellationToken)
    {
        _logger?.LogInfo($"{Name} thread started");

        if (_replicaManager == null || _networkManager == null)
        {
            _logger?.LogWarning($"{Name}: ReplicaManager or NetworkManager not available");
            return;
        }

        var isClient = string.Equals(_networkManager.Role, "client", StringComparison.OrdinalIgnoreCase);
        if (!isClient)
        {
            _logger?.LogInfo($"{Name}: server role — no explicit Subscribe needed");
            SubscriptionDemoPluginHost.Idle(cancellationToken);
            return;
        }

        _logger?.LogInfo($"{Name}: client — explicit Subscribe after 2s");

        try
        {
            Task.Delay(2000, cancellationToken).Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger?.LogInfo($"{Name}: sending explicit Subscribe(\"{PingNodePath}\")");
        _replicaManager.Subscribe(PingNodePath);
        SubscriptionDemoPluginHost.Idle(cancellationToken);
        _logger?.LogInfo($"{Name} thread exiting");
    }

    public void OnShutdown() => _logger?.LogInfo($"{Name} shutdown");
}

internal static class SubscriptionDemoPluginHost
{
    internal static void Idle(CancellationToken cancellationToken)
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
