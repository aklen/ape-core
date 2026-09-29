using Ape.Core.Config.Models;
using Ape.Core.Event;
using Ape.Core.Event.Services;
using Ape.Core.Logging;
using Ape.Core.Logging.Services;
using Ape.Core.Network;
using Ape.Core.Network.Services;
using Ape.Core.Replication;
using Ape.Core.Replication.Services;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>Minimal DI graph for integration tests that need a real <see cref="ISceneManager"/>.</summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal static class SceneIntegrationServices
{
    /// <summary>
    /// Builds and initializes Core scene stack (null network transport). Optionally registers <see cref="SceneCommitService"/>.
    /// </summary>
    public static IServiceProvider Build(bool includeSceneCommitPipeline = false)
    {
        var services = new ServiceCollection();
        var logger = new NullLoggerService();
        var eventManager = new EventManager();
        var networkManager = new NetworkManager("none");
        var replicaManager = new ReplicaManager();
        var sceneManager = new SceneManager();

        services.AddSingleton<IModuleTable>(new ModuleTable());
        services.AddSingleton<ILogger>(logger);
        services.AddSingleton<IEventManager>(eventManager);
        services.AddSingleton<INetworkManager>(networkManager);
        services.AddSingleton<IReplicaManager>(replicaManager);
        sceneManager.Register(services);

        SceneCommitService? commitService = null;
        if (includeSceneCommitPipeline)
        {
            commitService = new SceneCommitService();
            commitService.Register(services);
        }

        var sp = services.BuildServiceProvider();
        logger.Initialize(sp);
        eventManager.Initialize(sp);
        networkManager.Initialize(sp);
        replicaManager.Initialize(sp);
        sceneManager.Initialize(sp);
        commitService?.Initialize(sp);
        return sp;
    }
}
