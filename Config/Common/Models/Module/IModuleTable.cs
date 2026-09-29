namespace Ape.Core.Config.Models;

/// <summary>
/// The <c>modules</c> map on a config tree: sections, <c>enabled</c> flags, plugin/service assembly names.
/// </summary>
public interface IModuleTable
{
    /// <summary>Returns <c>modules[moduleId]</c> if present (may be an empty object).</summary>
    IConfigNode? GetModuleSection(IConfigNode? root, string moduleId);

    /// <summary>
    /// Whether <c>modules[moduleId]</c> is active. If the section is missing: core <c>Ape.Core.*</c> ids stay enabled;
    /// all other ids are disabled (opt-in — the key must exist under <c>modules</c> to run the module).
    /// If the section exists, <c>enabled: false</c> disables it; omitted <c>enabled</c> means <c>true</c>.
    /// </summary>
    bool IsModuleEnabled(IConfigNode? root, string moduleId);

    /// <summary>
    /// Plugin assembly names from <c>modules[*].plugins</c> (with <c>enabled</c> flags at module and entry level).
    /// </summary>
    IReadOnlyList<string> MergePluginNames(IConfigNode? root);

    /// <summary>
    /// Pluggable service DLL names from <c>modules[*].services</c> (with <c>enabled</c> flags).
    /// Only explicitly listed service keys contribute; there is no implicit per-module default and no extra allowlist.
    /// Keys may be full assembly names or short names matching the last segment of the parent module id
    /// (e.g. <c>Example</c> under <c>Ape.Module.Example</c> → <c>Ape.Module.Example</c>).
    /// Names must start with <c>Ape.Module.</c> or <c>Ape.Service.</c>.
    /// </summary>
    IReadOnlyList<string> MergePluggableServiceDllNames(IConfigNode? root);
}
