using Ape.Core.Logging;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Runtime.Plugin.Models;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Ape.Core.Runtime.Plugin.Services;

/// <summary>
/// Plugin manager with supervised execution.
/// Plugins run in isolated threads with exception handling and auto-restart support.
/// </summary>
public class PluginManager
{
    private readonly IServiceProvider _pluginServices;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PluginContext> _plugins = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _lockObject = new();

    public PluginManager(IServiceProvider services, ILogger logger)
    {
        _pluginServices = new PluginServiceProvider(services);
        _logger = logger;
    }

    /// <summary>
    /// Load a plugin from a DLL file.
    /// </summary>
    public void LoadPlugin(string dllPath)
    {
        try
        {
            if (!File.Exists(dllPath))
            {
                _logger.LogError($"Plugin DLL not found: {dllPath}");
                return;
            }

            var context = new PluginLoadContext(dllPath);
            var assembly = context.LoadFromAssemblyPath(dllPath);

            // Find types implementing IPlugin
            var pluginTypes = assembly.GetTypes()
                .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .ToList();

            if (pluginTypes.Count == 0)
            {
                _logger.LogWarning($"No plugin types found in {dllPath}");
                return;
            }

            foreach (var pluginType in pluginTypes)
            {
                var plugin = (IPlugin?)Activator.CreateInstance(pluginType);
                if (plugin == null)
                {
                    _logger.LogError($"Failed to create instance of {pluginType.Name}");
                    continue;
                }

                // Create plugin context
                var pluginContext = new PluginContext
                {
                    Plugin = plugin,
                    Thread = null!, // Set below
                    LoadContext = context,
                    AssemblyPath = dllPath,
                    State = PluginState.Loaded,
                    StartTime = DateTime.UtcNow
                };

                // Create supervised thread
                var thread = new Thread(() => SupervisedPluginEntry(pluginContext))
                {
                    Name = $"Plugin-{plugin.Name}",
                    IsBackground = true
                };

                pluginContext.Thread = thread;

                // Add to dictionary
                if (!_plugins.TryAdd(plugin.PluginId, pluginContext))
                {
                    _logger.LogError($"Plugin with ID '{plugin.PluginId}' already loaded");
                    continue;
                }

                thread.Start();

                _logger.LogInfo($"✅ Loaded plugin: {plugin.Name} ({plugin.PluginId}) - supervised mode");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading plugin from {dllPath}", ex);
        }
    }

    /// <summary>
    /// Load all plugins from a directory.
    /// </summary>
    public void LoadPluginsFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning($"Plugin directory not found: {directory}");
            return;
        }

        var dllFiles = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);

        _logger.LogInfo($"Loading plugins from {directory} ({dllFiles.Length} DLLs found)");

        foreach (var dllPath in dllFiles)
        {
            LoadPlugin(dllPath);
        }
    }

    /// <summary>
    /// Load specific plugins from config (by name, without .dll extension).
    /// </summary>
    public void LoadPluginsFromConfig(string directory, string[] pluginNames)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning($"Plugin directory not found: {directory}");
            return;
        }

        foreach (var pluginName in pluginNames)
        {
            var dllPath = Path.Combine(directory, $"{pluginName}.dll");

            if (!File.Exists(dllPath))
            {
                _logger.LogWarning($"Plugin not found: {pluginName} (expected at {dllPath})");
                continue;
            }

            LoadPlugin(dllPath);
        }
    }

    /// <summary>
    /// Shutdown all plugins and wait for threads to exit.
    /// </summary>
    public void Shutdown()
    {
        _logger.LogInfo("Shutting down plugins...");

        // Signal cancellation
        _cancellationTokenSource.Cancel();

        // Wait for threads to exit
        foreach (var ctx in _plugins.Values)
        {
            try
            {
                // OnShutdown is called in SupervisedPluginEntry's finally block

                if (ctx.Thread.IsAlive)
                {
                    ctx.Thread.Join(TimeSpan.FromSeconds(5));

                    if (ctx.Thread.IsAlive)
                    {
                        // Thread-safety: Do NOT unload if thread is still running!
                        // Unloading while thread is active would cause access violations
                        _logger.LogWarning(
                            $"⚠️ Plugin {ctx.Plugin.Name} did not exit after 5s timeout. " +
                            $"Skipping unload to avoid crashes. Thread will be orphaned.");
                        continue;  // Skip unload - better to leak than crash
                    }
                }

                ctx.LoadContext.Unload();

                _logger.LogInfo($"✅ Unloaded plugin: {ctx.Plugin.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error shutting down plugin {ctx.Plugin.Name}", ex);
            }
        }

        _plugins.Clear();
    }

    /// <summary>
    /// Supervised plugin thread entry point.
    /// Catches all exceptions and manages plugin lifecycle.
    /// </summary>
    private void SupervisedPluginEntry(PluginContext ctx)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Initialize plugin
            ctx.State = PluginState.Initializing;
            _logger.LogDebug($"[{ctx.Plugin.Name}] Initializing...");

            ctx.Plugin.OnInit(_pluginServices);

            // Run plugin
            ctx.State = PluginState.Running;
            _logger.LogDebug($"[{ctx.Plugin.Name}] Running...");

            ctx.Plugin.OnRun(_cancellationTokenSource.Token);

            // Clean exit
            ctx.State = PluginState.Stopped;
            var uptime = DateTime.UtcNow - startTime;
            _logger.LogInfo($"✅ [{ctx.Plugin.Name}] Stopped cleanly (uptime: {uptime:hh\\:mm\\:ss})");
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
            ctx.State = PluginState.Stopped;
            _logger.LogInfo($"[{ctx.Plugin.Name}] Cancelled by shutdown");
        }
        catch (Exception ex)
        {
            // Plugin crashed!
            ctx.State = PluginState.Crashed;
            ctx.LastError = ex.ToString();
            ctx.LastCrashTime = DateTime.UtcNow;
            ctx.CrashCount++;

            var uptime = DateTime.UtcNow - startTime;

            _logger.LogError($"💥 [{ctx.Plugin.Name}] CRASHED after {uptime:hh\\:mm\\:ss}");
            _logger.LogError($"   Error: {ex.GetType().Name}: {ex.Message}");
            _logger.LogError($"   Stack trace:\n{ex.StackTrace}");

            // Auto-restart logic
            if (ctx.RestartEnabled && ctx.CrashCount <= ctx.MaxRestarts)
            {
                _logger.LogWarning($"🔄 [{ctx.Plugin.Name}] Scheduling restart (attempt {ctx.CrashCount}/{ctx.MaxRestarts})...");

                // Wait 5 seconds before restart
                Task.Delay(5000).ContinueWith(_ => RestartPlugin(ctx.Plugin.PluginId));
            }
            else
            {
                _logger.LogError($"❌ [{ctx.Plugin.Name}] Permanently disabled after {ctx.CrashCount} crashes");
            }
        }
        finally
        {
            // Always call OnShutdown (even after crash)
            try
            {
                ctx.Plugin.OnShutdown();
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{ctx.Plugin.Name}] Error in OnShutdown()", ex);
            }
        }
    }

    /// <summary>
    /// Restart a crashed plugin.
    /// </summary>
    private void RestartPlugin(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var ctx))
        {
            _logger.LogError($"Cannot restart plugin '{pluginId}' - not found");
            return;
        }

        lock (_lockObject)
        {
            if (ctx.State == PluginState.Crashed)
            {
                _logger.LogInfo($"🔄 Restarting plugin: {ctx.Plugin.Name}");

                // Create new thread
                var newThread = new Thread(() => SupervisedPluginEntry(ctx))
                {
                    Name = $"Plugin-{ctx.Plugin.Name}-Restart-{ctx.CrashCount}",
                    IsBackground = true
                };

                ctx.Thread = newThread;
                ctx.State = PluginState.Loaded;
                ctx.LastError = null;

                newThread.Start();
            }
        }
    }

    /// <summary>
    /// Get status of all plugins (for monitoring/diagnostics).
    /// </summary>
    public IReadOnlyList<PluginStatus> GetPluginStatuses()
    {
        return _plugins.Values.Select(ctx => new PluginStatus
        {
            Name = ctx.Plugin.Name,
            PluginId = ctx.Plugin.PluginId,
            State = ctx.State.ToString(),
            LastError = ctx.LastError,
            LastCrashTime = ctx.LastCrashTime,
            CrashCount = ctx.CrashCount,
            ThreadName = ctx.Thread.Name,
            ThreadAlive = ctx.Thread.IsAlive,
            StartTime = ctx.StartTime
        }).ToList();
    }

    /// <summary>
    /// Internal context for a loaded plugin.
    /// </summary>
    private class PluginContext
    {
        public required IPlugin Plugin { get; init; }
        public required Thread Thread { get; set; }
        public required PluginLoadContext LoadContext { get; init; }
        public required string AssemblyPath { get; init; }

        public PluginState State { get; set; } = PluginState.Loaded;
        public string? LastError { get; set; }
        public DateTime? LastCrashTime { get; set; }
        public int CrashCount { get; set; }
        public bool RestartEnabled { get; set; } = true;
        public int MaxRestarts { get; set; } = 3;
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
    }
}

/// <summary>
/// Custom AssemblyLoadContext for plugin isolation.
/// Allows hot-reload and unloading of plugin assemblies.
/// </summary>
internal class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (UseDefaultLoadContext(assemblyName))
            return null;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Shared into the default <see cref="AssemblyLoadContext"/>; module *plugin* assemblies
    /// (<c>Ape.Module.*.Plugin.*</c>) stay in this collectible ALC.
    /// </summary>
    private static bool UseDefaultLoadContext(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.StartsWith("Ape.Core", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("MessagePack", StringComparison.Ordinal))
            return true;

        if (name.StartsWith("Ape.Module.", StringComparison.Ordinal))
        {
            if (name.Contains(".Plugin.", StringComparison.Ordinal))
                return false;
            return true;
        }

        return false;
    }
}
