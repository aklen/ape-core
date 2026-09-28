using System.Linq;
using Ape.Core.Config;
using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Scene;

namespace Ape.Core.Config.Models;

/// <summary>
/// Interprets the <c>modules</c> map on a config tree.
/// Core <c>Ape.Core.*</c> module ids may be enabled implicitly when <c>modules[id]</c> is absent; all other ids require an explicit <c>modules</c> entry (opt-in).
/// </summary>
public sealed class ModuleTable : IModuleTable
{
    /// <summary>
    /// When <c>modules[moduleId]</c> is missing, these ids still count as enabled (engine always wires core subsystems).
    /// Every other module id requires a <c>modules</c> entry to run (plugins/services under it follow existing <c>enabled</c> rules).
    /// </summary>
    private static readonly HashSet<string> CoreModuleIdsEnabledWhenSectionMissing = new(StringComparer.Ordinal)
    {
        LoggingModuleIds.ModuleId,
        EventModuleIds.ModuleId,
        ConfigModuleIds.ModuleId,
        NetworkModuleIds.ModuleId,
        ReplicaModuleIds.ModuleId,
        SceneModuleIds.ModuleId,
        PluginModuleIds.ModuleId,
    };

    /// <inheritdoc />
    public IConfigNode? GetModuleSection(IConfigNode? root, string moduleId)
    {
        if (root == null || string.IsNullOrWhiteSpace(moduleId))
            return null;
        if (!root.TryGetChildObject("modules", out var modules))
            return null;
        return modules.TryGetChildObject(moduleId, out var node) ? node : null;
    }

    /// <inheritdoc />
    public bool IsModuleEnabled(IConfigNode? root, string moduleId)
    {
        var section = GetModuleSection(root, moduleId);
        if (section == null)
            return CoreModuleIdsEnabledWhenSectionMissing.Contains(moduleId);
        return IsModuleEntryEnabled(section);
    }

    /// <inheritdoc />
    public IReadOnlyList<ModulePluginSpec> MergePluginSpecs(IConfigNode? root)
    {
        var list = new List<ModulePluginSpec>();
        if (root == null || !root.TryGetChildObject("modules", out var modules))
            return list;

        foreach (var moduleId in modules.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!modules.TryGetChildObject(moduleId, out var mod))
                continue;
            if (!IsModuleEntryEnabled(mod))
                continue;
            if (!mod.TryGetChildObject("plugins", out var plug))
                continue;

            foreach (var pluginKey in plug.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(pluginKey))
                    continue;
                if (plug.TryGetChildObject(pluginKey, out var entry) && !IsPluginOrServiceEntryEnabled(entry))
                    continue;
                list.Add(new ModulePluginSpec(moduleId, pluginKey));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> MergePluginNames(IConfigNode? root)
    {
        return MergePluginSpecs(root)
            .Select(s => ResolveModulePluginAssemblyName(s.ModuleId, s.PluginKey))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc cref="IModuleTable.MergePluggableServiceDllNames" />
    public IReadOnlyList<string> MergePluggableServiceDllNames(IConfigNode? root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        MergePluggableServiceNamesFromModules(root, set);
        return SortOrdinal(set);
    }

    private static void MergePluggableServiceNamesFromModules(
        IConfigNode? root,
        HashSet<string> set)
    {
        if (root == null || !root.TryGetChildObject("modules", out var modules))
            return;

        foreach (var moduleId in modules.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!modules.TryGetChildObject(moduleId, out var mod))
                continue;
            if (!IsModuleEntryEnabled(mod))
                continue;
            if (!mod.TryGetChildObject("services", out var svc))
                continue;

            foreach (var serviceKey in svc.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(serviceKey))
                    continue;
                var resolvedDllName = ResolveModuleServiceAssemblyName(moduleId, serviceKey);
                if (!IsPluggableServiceDllName(resolvedDllName))
                    continue;
                if (!svc.TryGetChildObject(serviceKey, out var entry))
                {
                    set.Add(resolvedDllName);
                    continue;
                }

                if (!IsPluginOrServiceEntryEnabled(entry))
                    continue;
                set.Add(resolvedDllName);
            }
        }
    }

    /// <summary>
    /// <c>modules[moduleId]</c> object: disabled when <c>enabled</c> is explicitly <c>false</c>.
    /// </summary>
    private static bool IsModuleEntryEnabled(IConfigNode section)
    {
        if (!section.HasKey("enabled"))
            return true;
        return section.GetBool("enabled", true);
    }

    /// <summary>Plugin or pluggable-service entry object under <c>plugins</c> / <c>services</c>.</summary>
    private static bool IsPluginOrServiceEntryEnabled(IConfigNode entry)
    {
        if (!entry.HasKey("enabled"))
            return true;
        return entry.GetBool("enabled", true);
    }

    private static IReadOnlyList<string> SortOrdinal(HashSet<string> set) =>
        set.OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>
    /// <c>modules[moduleId].services</c> key → pluggable service DLL / assembly name.
    /// Full name when the key contains <c>'.'</c>; otherwise short keys that match the last segment of
    /// <paramref name="moduleId"/> resolve to <paramref name="moduleId"/> (primary DLL for that module).
    /// </summary>
    private static string ResolveModuleServiceAssemblyName(string moduleId, string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
            return serviceKey;
        if (serviceKey.Contains('.'))
            return serviceKey;
        var lastDot = moduleId.LastIndexOf('.');
        var lastSegment = lastDot >= 0 ? moduleId[(lastDot + 1)..] : moduleId;
        return string.Equals(serviceKey, lastSegment, StringComparison.Ordinal)
            ? moduleId
            : $"{moduleId}.{serviceKey}";
    }

    /// <summary>
    /// Short plugin key (no dots) under <c>modules[moduleId].plugins</c> → full assembly name.
    /// </summary>
    private static string ResolveModulePluginAssemblyName(string moduleId, string pluginKey)
    {
        if (pluginKey.Contains('.'))
            return pluginKey;
        return $"{moduleId}.Plugin.{pluginKey}";
    }

    private static bool IsPluggableServiceDllName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith("Ape.Service.", StringComparison.Ordinal)
         || name.StartsWith("Ape.Module.", StringComparison.Ordinal));
}
