using System.Runtime.Versioning;
using Ape.Core.Config.Models;
using Ape.Core.Determinism;
using Ape.Core.Event;
using Ape.Core.Event.Services;
using Ape.Core.Logging;
using Ape.Core.Logging.Services;
using Ape.Core.Network;
using Ape.Core.Network.Services;
using Ape.Core.Replication;
using Ape.Core.Replication.Services;
using Ape.Core.Scene;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Owns a fresh, local Scene and host for replay. This never advances the production host.
/// Module-specific entity registration can be supplied without Core naming any module.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class IsolatedSceneReplayHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly SceneCommitService _host;
    private bool _disposed;

    public IsolatedSceneReplayHost(Action<IServiceProvider>? registerSceneEntities = null)
    {
        var services = new ServiceCollection();
        var logger = new NullLoggerService();
        var events = new EventManager();
        var network = new NetworkManager("none");
        var replicas = new ReplicaManager();
        var sceneManager = new SceneManager();
        var host = new SceneCommitService();
        services.AddSingleton<IModuleTable>(new ModuleTable());
        services.AddSingleton<ILogger>(logger);
        services.AddSingleton<IEventManager>(events);
        services.AddSingleton<INetworkManager>(network);
        services.AddSingleton<IReplicaManager>(replicas);
        sceneManager.Register(services);
        host.Register(services);

        var provider = services.BuildServiceProvider();
        try
        {
            logger.Initialize(provider);
            events.Initialize(provider);
            network.Initialize(provider);
            replicas.Initialize(provider);
            sceneManager.Initialize(provider);
            registerSceneEntities?.Invoke(provider);
            host.Initialize(provider);
            Scene = provider.GetRequiredService<ISceneRead>();
            Logger = logger;
            _host = host;
            _services = provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    public ISceneRead Scene { get; }

    public ILogger Logger { get; }

    public IReadOnlyList<HostFrameRecord> Records => _host.Records;

    public void Register(IDeterministicFrameParticipant participant)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.Register(participant);
    }

    public HostFrameResult Run(SampleFrame sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sample);
        if (_host.FrameId == 0 && _host.Records.Count == 0 && sample.FrameId > 1)
            _host.AlignFirstReplayFrame(sample.FrameId);
        if (sample.FrameId != _host.FrameId + 1)
            throw new InvalidDataException(
                $"Replay expected frame {_host.FrameId + 1}, got {sample.FrameId}.");
        return _host.RunNextFrame(sample.LogicalTime);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.Stop();
        _services.Dispose();
    }
}
