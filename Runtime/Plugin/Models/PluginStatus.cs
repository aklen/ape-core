namespace Ape.Core.Runtime.Plugin.Models;

/// <summary>
/// Public status information about a plugin.
/// Used for health monitoring and diagnostics.
/// </summary>
public class PluginStatus
{
    public string Name { get; init; } = "";
    public string PluginId { get; init; } = "";
    public string State { get; init; } = "";
    public string? LastError { get; init; }
    public DateTime? LastCrashTime { get; init; }
    public int CrashCount { get; init; }
    public string? ThreadName { get; init; }
    public bool ThreadAlive { get; init; }
    public DateTime StartTime { get; init; }
}
