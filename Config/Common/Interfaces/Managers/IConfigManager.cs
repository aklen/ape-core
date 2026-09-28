using Ape.Core.Config.Models;

namespace Ape.Core.Config;

/// <summary>
/// Interface for configuration management.
/// Loads and parses JSON configuration files into ConfigNode hierarchy.
/// </summary>
public interface IConfigManager
{
    /// <summary>
    /// Primary startup config tree (same as <see cref="IStartupConfig.Root"/>).
    /// Does not include files loaded later via <see cref="LoadJson(string, ConfigNode)"/>.
    /// </summary>
    IConfigNode? StartupRoot { get; }

    /// <summary>
    /// Load a JSON configuration file and parse it into a ConfigNode.
    /// </summary>
    /// <param name="filePath">Absolute path to the JSON file</param>
    /// <param name="config">ConfigNode to populate with parsed data</param>
    /// <returns>True if successful, false on error</returns>
    bool LoadJson(string filePath, out ConfigNode config);

    /// <summary>
    /// Load a JSON configuration file and parse it into an existing ConfigNode.
    /// </summary>
    /// <param name="filePath">Absolute path to the JSON file</param>
    /// <param name="config">Existing ConfigNode to populate</param>
    /// <returns>True if successful, false on error</returns>
    bool LoadJson(string filePath, ConfigNode config);
}
