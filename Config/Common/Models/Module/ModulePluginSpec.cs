namespace Ape.Core.Config.Models;

/// <summary>
/// One enabled <c>modules[moduleId].plugins[pluginKey]</c> entry.
/// </summary>
public readonly record struct ModulePluginSpec(string ModuleId, string PluginKey);
