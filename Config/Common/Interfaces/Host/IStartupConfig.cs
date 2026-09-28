using Ape.Core.Config.Models;

namespace Ape.Core.Config;

/// <summary>
/// Snapshot of the process startup JSON (single parse). Core services and plugins should read
/// module sections from <see cref="Root"/> (typically under <c>modules["…"]</c>) instead of reloading the file.
/// </summary>
public interface IStartupConfig
{
    /// <summary>Root of the startup config file, or null if the process started without a config path.</summary>
    IConfigNode? Root { get; }
}
