using System.Reflection;
using System.Runtime.Loader;
using Ape.Core.Config;
using Ape.Core.Config.Models;
using Ape.Core.Config.Services;
using Ape.Core.Event.Services;
using Ape.Core.Logging;
using Ape.Core.Logging.Services;
using Ape.Core.Network;
using Ape.Core.Network.Services;
using Ape.Core.Replication;
using Ape.Core.Replication.Services;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Runtime.Plugin.Services;
using Ape.Core.Runtime.Service;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core;

/// <summary>
/// Process-wide runtime entry — <c>Start</c> / <c>Stop</c>.
/// An embedder (or <c>Ape.Launcher</c>) references <c>Ape.Core</c> as a library and starts the engine
/// with a host JSON; modules are loaded from DLLs next to the process (config <c>modules</c> keys),
/// not compiled into Core.
/// </summary>
public static class ApeSystem
{
    private const string DefaultLogTransport = "console";
    private static readonly object Gate = new();

    private static CancellationTokenSource? _cts;
    private static IReadOnlyList<IService>? _bootstrap;
    private static IServiceProvider? _services;
    private static PluginManager? _pluginManager;
    private static INetworkManager? _networkManager;
    private static bool _networkStarted;
    private static bool _blocking;
    private static volatile bool _started;

    /// <summary>Root DI container after a successful <see cref="Start"/>; null when stopped.</summary>
    public static IServiceProvider? Services
    {
        get
        {
            lock (Gate)
                return _services;
        }
    }

    /// <summary>
    /// Start Core, then enabled <c>Ape.Module.*</c> services from DLLs beside the process,
    /// then <c>plugins/</c> listed in config.
    /// </summary>
    /// <param name="configPath">Host JSON path (optional).</param>
    /// <param name="blocking">
    /// <see langword="true"/> (typical for <c>Ape.Launcher</c>): run the tick loop until Ctrl+C or
    /// <see cref="Stop"/>, then shut down before returning.
    /// <see langword="false"/> (embedders): return after plugins start; caller owns the thread and must call <see cref="Stop"/>.
    /// </param>
    /// <param name="userThread">Optional work started after plugins (C++ <c>userThreadFunction</c>).</param>
    public static void Start(string? configPath, bool blocking = true, Action? userThread = null)
    {
        lock (Gate)
        {
            if (_started)
                throw new InvalidOperationException("ApeSystem.Start has already been called; call Stop first.");
            _blocking = blocking;
            Bootstrap(configPath);
            _started = true;
        }

        if (userThread != null)
            _ = Task.Run(userThread);

        if (!blocking)
            return;

        try
        {
            WaitUntilStopRequested();
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Stop plugins and services. Safe to call when not started.</summary>
    public static void Stop()
    {
        _cts?.Cancel();
        if (_blocking)
            return;
        Shutdown();
    }

    private static void Bootstrap(string? configPath)
    {
        var preConfigLogger = new NullLoggerService();
        var tempConfigManager = new ConfigManager();
        tempConfigManager.Initialize(new ServiceCollection().AddSingleton<ILogger>(preConfigLogger).BuildServiceProvider());
        var moduleTable = new ModuleTable();
        var coreConfig = configPath != null ? LoadCoreConfig(configPath, tempConfigManager, preConfigLogger) : null;
        if (configPath != null)
            Environment.SetEnvironmentVariable("APE_CONFIG_PATH", Path.GetFullPath(configPath));

        var loggingModuleEnabled = moduleTable.IsModuleEnabled(coreConfig, LoggingModuleIds.ModuleId);
        var loggingSection = loggingModuleEnabled
            ? moduleTable.GetModuleSection(coreConfig, LoggingModuleIds.ModuleId)
            : null;
        var logTransportFromModule = loggingSection?.GetString("transport");
        var logTransportType = string.IsNullOrWhiteSpace(logTransportFromModule)
            ? DefaultLogTransport
            : logTransportFromModule;

        ILogger tempLogger = loggingModuleEnabled
            ? new LogManager(logTransportType)
            : new NullLoggerService();

        if (loggingModuleEnabled)
        {
            tempLogger.LogInfo("=== Ape.Core ===");
            tempLogger.LogInfo($"Runtime: {Environment.Version}");
            tempLogger.LogInfo($"Platform: {Environment.OSVersion.Platform}");
            tempLogger.LogInfo($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            tempLogger.LogInfo("");
        }

        if (loggingModuleEnabled && coreConfig != null && configPath != null)
            LogCoreConfigLoadedSummary(tempLogger, configPath, coreConfig, moduleTable);

        var networkSection = moduleTable.GetModuleSection(coreConfig, NetworkModuleIds.ModuleId);
        var networkModuleEnabled = moduleTable.IsModuleEnabled(coreConfig, NetworkModuleIds.ModuleId);
        var enableNetwork = networkModuleEnabled && (networkSection?.GetBool("enabled") ?? false);
        var networkTransportType = enableNetwork
            ? (networkSection?.GetString("transport", "none") ?? "none")
            : "none";

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IModuleTable>(moduleTable);
        serviceCollection.AddSingleton<IStartupConfig>(_ => new StartupConfig(coreConfig));

        var replicaSection = moduleTable.IsModuleEnabled(coreConfig, ReplicaModuleIds.ModuleId)
            ? moduleTable.GetModuleSection(coreConfig, ReplicaModuleIds.ModuleId)
            : null;

        var bootstrap = new List<IService>();
        if (loggingModuleEnabled)
            bootstrap.Add(new LogManager(logTransportType));
        else
            bootstrap.Add(new NullLoggerService());

        bootstrap.Add(new EventManager());
        bootstrap.Add(new ConfigManager());
        bootstrap.Add(new NetworkManager(networkTransportType));
        bootstrap.Add(new ReplicaAccessControlService(replicaSection));
        bootstrap.Add(new ReplicaManager());
        bootstrap.Add(new SceneManager());
        bootstrap.Add(new SceneCommitService());

        var loadedServiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in bootstrap)
            loadedServiceIds.Add(s.ServiceId);

        foreach (var moduleService in LoadModulePluggableServices(coreConfig, moduleTable, tempLogger))
        {
            if (!loadedServiceIds.Add(moduleService.ServiceId))
                continue;
            bootstrap.Add(moduleService);
        }

        var pluggableServiceDlls = moduleTable.MergePluggableServiceDllNames(coreConfig);
        var serviceDir = Path.Combine(AppContext.BaseDirectory, "services");
        Directory.CreateDirectory(serviceDir);

        foreach (var serviceName in pluggableServiceDlls)
        {
            if (bootstrap.Any(s => string.Equals(s.ServiceId, serviceName, StringComparison.Ordinal) ||
                                   string.Equals(s.GetType().Assembly.GetName().Name, serviceName, StringComparison.Ordinal)))
                continue;

            var dllPath = Path.Combine(serviceDir, $"{serviceName}.dll");
            if (!File.Exists(dllPath))
            {
                tempLogger.LogWarning($"Service not found: {serviceName}");
                continue;
            }

            var loaded = LoadServiceDll(dllPath, tempLogger);
            if (loaded is IPluggableService p && loadedServiceIds.Add(p.ServiceId))
                bootstrap.Add(p);
        }

        tempLogger.LogInfo("Registering services...");
        foreach (var service in bootstrap)
        {
            service.Register(serviceCollection);
            tempLogger.LogInfo($"  ✅ {service.Name} registered");
        }

        tempLogger.LogInfo("");
        tempLogger.LogInfo("Building service provider...");
        var services = serviceCollection.BuildServiceProvider();
        var logger = services.GetRequiredService<ILogger>();

        tempLogger.LogInfo("✅ Service provider built");
        tempLogger.LogInfo($"  - Core: {bootstrap.Count(static s => s is ICoreService)}");
        tempLogger.LogInfo($"  - Pluggable: {bootstrap.Count(s => s is IPluggableService)}");

        tempLogger.LogInfo("");
        tempLogger.LogInfo("Initializing services...");

        foreach (var service in bootstrap)
        {
            service.Initialize(services);
            logger.LogInfo($"  ✅ {service.Name} initialized");
        }

        logger.LogInfo("");
        var role = networkSection?.GetString("role") ?? "server";
        var networkManager = services.GetRequiredService<INetworkManager>();
        var replicaManager = services.GetRequiredService<IReplicaManager>();

        if (enableNetwork)
        {
            var port = networkSection?.GetInt("port") ?? 9050;
            var peerName = networkSection?.GetString("peerName");

            if (role == "client")
            {
                var host = networkSection?.GetString("host", "localhost") ?? "localhost";
                networkManager.Connect(host, port, peerName, role);
                logger.LogInfo($"Network client connecting to {host}:{port}");
            }
            else
            {
                networkManager.StartServer(port, peerName, role);
                logger.LogInfo($"Network server started on port {port}");
            }

            _networkStarted = true;
        }

        logger.LogInfo("");
        _cts = new CancellationTokenSource();

        foreach (var service in bootstrap)
            service.Start(_cts.Token);

        logger.LogInfo("");
        var pluginManager = new PluginManager(services, logger);
        var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        Directory.CreateDirectory(pluginDir);

        var pluginNames = moduleTable.MergePluginNames(coreConfig);
        if (pluginNames.Count > 0)
        {
            logger.LogInfo($"Loading {pluginNames.Count} plugin(s) from config...");
            pluginManager.LoadPluginsFromConfig(pluginDir, pluginNames.ToArray());
        }

        logger.LogInfo("");
        logger.LogInfo("ApeCore running. Press Ctrl+C to exit.");
        logger.LogInfo("");

        _bootstrap = bootstrap;
        _services = services;
        _pluginManager = pluginManager;
        _networkManager = networkManager;
        _enableNetwork = enableNetwork;
        _replicaManager = replicaManager;
    }

    private static bool _enableNetwork;
    private static IReplicaManager? _replicaManager;

    private static void WaitUntilStopRequested()
    {
        var running = true;
        ConsoleCancelEventHandler? handler = null;
        handler = (_, e) =>
        {
            e.Cancel = true;
            running = false;
            _cts?.Cancel();
            _services?.GetService<ILogger>()?.LogInfo("Shutdown requested...");
        };

        try
        {
            Console.CancelKeyPress += handler;
        }
        catch (IOException)
        {
            // No console (embedded / test host).
        }

                    var lastTick = DateTime.UtcNow;
        var tickRate = TimeSpan.FromMilliseconds(100);
        var frameRunner = _services!.GetRequiredService<IHostFrameRunner>();

        try
        {
            while (running && _cts is { IsCancellationRequested: false })
            {
                var now = DateTime.UtcNow;
                if (now - lastTick >= tickRate)
                {
                    lastTick = now;
                    if (_enableNetwork)
                        _networkManager?.PollEvents();
                    try
                    {
                        frameRunner.RunNextFrame();
                    }
                    catch (InvalidOperationException ex)
                    {
                        _services.GetService<ILogger>()?.LogError(
                            $"Host frame pipeline stopped: {ex.Message}", ex);
                        running = false;
                    }
                }

                Thread.Sleep(10);
            }
        }
        finally
        {
            if (handler != null)
            {
                try
                {
                    Console.CancelKeyPress -= handler;
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static void Shutdown()
    {
        lock (Gate)
        {
            if (!_started && _bootstrap == null)
                return;

            var logger = _services?.GetService<ILogger>();
            logger?.LogInfo("Shutting down...");
            logger?.LogInfo("Stopping services...");

            if (_bootstrap != null)
            {
                for (var i = _bootstrap.Count - 1; i >= 0; i--)
                {
                    var s = _bootstrap[i];
                    s.Stop();
                    logger?.LogInfo($"Stopped service: {s.Name}");
                }
            }

            _pluginManager?.Shutdown();

            if (_networkStarted)
                _networkManager?.Stop();

            logger?.LogInfo("ApeCore shutdown complete.");

            if (_services is IDisposable disposable)
                disposable.Dispose();

            _cts?.Dispose();
            _cts = null;
            _bootstrap = null;
            _services = null;
            _pluginManager = null;
            _networkManager = null;
            _replicaManager = null;
            _networkStarted = false;
            _enableNetwork = false;
            _started = false;
            _blocking = false;
        }
    }

    private static IEnumerable<IPluggableService> LoadModulePluggableServices(
        IConfigNode? coreConfig,
        IModuleTable moduleTable,
        ILogger logger)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var moduleId in EnabledApeModuleIds(coreConfig, moduleTable))
        {
            var dllPath = Path.Combine(baseDir, $"{moduleId}.dll");
            if (!File.Exists(dllPath))
            {
                logger.LogDebug($"Module assembly not beside process: {dllPath}");
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to load module {moduleId}: {ex.Message}");
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }

            foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;
                if (!typeof(IPluggableService).IsAssignableFrom(type))
                    continue;

                IPluggableService? instance;
                try
                {
                    instance = Activator.CreateInstance(type) as IPluggableService;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not create {type.FullName}: {ex.Message}");
                    continue;
                }

                if (instance != null)
                    yield return instance;
            }
        }
    }

    private static IEnumerable<string> EnabledApeModuleIds(IConfigNode? root, IModuleTable moduleTable)
    {
        if (root == null || !root.TryGetChildObject("modules", out var modules))
            yield break;

        foreach (var moduleId in modules.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!moduleId.StartsWith("Ape.Module.", StringComparison.Ordinal))
                continue;
            if (moduleId.Contains(".Plugin.", StringComparison.Ordinal))
                continue;
            if (!moduleTable.IsModuleEnabled(root, moduleId))
                continue;
            yield return moduleId;
        }
    }

    private static IService? LoadServiceDll(string dllPath, ILogger logger)
    {
        try
        {
            var context = new ServiceLoadContext(dllPath);
            var assembly = context.LoadFromAssemblyPath(dllPath);
            var serviceTypes = assembly.GetTypes()
                .Where(t => typeof(IService).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

            if (serviceTypes.Count == 0)
            {
                logger.LogWarning($"No service types found in {dllPath}");
                return null;
            }

            return (IService?)Activator.CreateInstance(serviceTypes[0]);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error loading service from {dllPath}: {ex.Message}");
            return null;
        }
    }

    private static ConfigNode? LoadCoreConfig(string configPath, IConfigManager configManager, ILogger logger)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                var msg = $"Config file not found: {configPath}";
                logger.LogError(msg);
                Console.Error.WriteLine(msg);
                return null;
            }

            if (configManager.LoadJson(configPath, out var config))
                return config;

            const string parseFail = "Failed to parse core config";
            logger.LogError(parseFail);
            Console.Error.WriteLine(parseFail);
            return null;
        }
        catch (Exception ex)
        {
            var msg = $"Failed to load core config: {ex.Message}";
            logger.LogError(msg, ex);
            Console.Error.WriteLine(msg);
            return null;
        }
    }

    private static void LogCoreConfigLoadedSummary(ILogger logger, string configPath, IConfigNode config, IModuleTable moduleTable)
    {
        logger.LogInfo($"✅ Core config loaded successfully: {configPath}");

        var svcDll = moduleTable.MergePluggableServiceDllNames(config);
        if (svcDll.Count > 0)
            logger.LogInfo($"   - Pluggable services (DLL): {string.Join(", ", svcDll)}");

        var plug = moduleTable.MergePluginNames(config);
        if (plug.Count > 0)
            logger.LogInfo($"   - Plugins: {string.Join(", ", plug)}");

        var networkConfig = moduleTable.GetModuleSection(config, NetworkModuleIds.ModuleId);
        var networkModuleOn = moduleTable.IsModuleEnabled(config, NetworkModuleIds.ModuleId);
        var networkEnabled = networkModuleOn && (networkConfig?.GetBool("enabled") ?? false);
        var networkPort = networkConfig?.GetInt("port") ?? 9050;
        logger.LogInfo($"   - Network: {(networkEnabled ? "Enabled" : "Disabled")} (port: {networkPort})");
    }
}
