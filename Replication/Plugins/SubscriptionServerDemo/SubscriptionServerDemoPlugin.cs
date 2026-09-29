using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Scene.Commit;

namespace Ape.Core.Replication.Plugin.SubscriptionServerDemo;

/// <summary>
/// Server-side subscription demo: host frames create <c>/PingNode</c> and update position so replicas flow to clients.
/// </summary>
public sealed class SubscriptionServerDemoPlugin : IPlugin, IDeterministicFrameParticipant
{
    public const string PingNodeName = "PingNode";

    public string PluginId => "subscription-server-demo";
    public string Name => "Subscription Server Demo";

    public string ParticipantId => PluginId;

    public FramePhase Phase => FramePhase.Publish;

    public int Order => 100;

    private INetworkManager? _networkManager;
    private ILogger? _logger;
    private bool _authoritative;
    private bool _createQueued;

    public void OnInit(IServiceProvider services)
    {
        _networkManager = services.GetService(typeof(INetworkManager)) as INetworkManager;
        _logger = services.GetService(typeof(ILogger)) as ILogger;
        var registry = services.GetService(typeof(IFrameParticipantRegistry)) as IFrameParticipantRegistry;
        _authoritative = _networkManager != null
            && !string.Equals(_networkManager.Role, "client", StringComparison.OrdinalIgnoreCase);
        if (_authoritative)
            registry?.Register(this);
        _logger?.LogInfo($"{Name} initialized");
    }

    public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
    {
        if (!_authoritative)
            return;

        if (!_createQueued)
        {
            commits.Enqueue(new CreateSceneNodeCommitRequest(PingNodeName, null, Vector3.Zero));
            _createQueued = true;
            _logger?.LogInfo($"{Name}: queued create /{PingNodeName}");
            return;
        }

        var t = context.FrameId * 0.05f;
        var pos = new Vector3(MathF.Sin(t) * 3f, context.FrameId * 0.01f, MathF.Cos(t) * 3f);
        commits.Enqueue(new SetSceneNodePositionCommitRequest(PingNodeName, pos));
    }

    public void OnRun(CancellationToken cancellationToken)
    {
        _logger?.LogInfo($"{Name} thread started");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task.Delay(200, cancellationToken).Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger?.LogInfo($"{Name} thread exiting");
    }

    public void OnShutdown() => _logger?.LogInfo($"{Name} shutdown");
}
