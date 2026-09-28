namespace Ape.Core.Runtime.Plugin;

/// <summary>
/// Base interface for all ApeCore plugins.
/// Plugins run in their own threads and communicate via the EventManager.
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Unique identifier for this plugin instance.
    /// </summary>
    string PluginId { get; }

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Called once when the plugin is loaded, before the thread starts.
    /// Use this for dependency injection and initialization.
    /// </summary>
    /// <param name="services">DI service provider</param>
    void OnInit(IServiceProvider services);

    /// <summary>
    /// Plugin's main execution loop. Runs in a dedicated thread.
    /// Should check the cancellation token periodically and exit cleanly when requested.
    /// </summary>
    /// <param name="cancellationToken">Token to signal plugin shutdown</param>
    void OnRun(CancellationToken cancellationToken);

    /// <summary>
    /// Called when the plugin is being unloaded.
    /// Clean up resources here.
    /// </summary>
    void OnShutdown();
}
