namespace Ape.Core.Runtime.Plugin.Models;

/// <summary>
/// Plugin lifecycle states.
/// </summary>
public enum PluginState
{
    /// <summary>
    /// Plugin DLL loaded, instance created.
    /// </summary>
    Loaded,

    /// <summary>
    /// OnInit() is being called.
    /// </summary>
    Initializing,

    /// <summary>
    /// OnRun() is executing in a thread.
    /// </summary>
    Running,

    /// <summary>
    /// Plugin crashed with an unhandled exception.
    /// </summary>
    Crashed,

    /// <summary>
    /// Plugin stopped cleanly (OnRun() returned or was cancelled).
    /// </summary>
    Stopped
}
