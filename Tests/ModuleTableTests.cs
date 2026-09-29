using Ape.Core.Config.Models;
using Ape.Core.Logging;

namespace Ape.Core.Tests;

public class ModuleTableTests
{
    private readonly ModuleTable _sut = new();

    [Fact]
    public void IsModuleEnabled_missing_section_true_for_core_module()
    {
        var root = new ConfigNode();
        Assert.True(_sut.IsModuleEnabled(root, LoggingModuleIds.ModuleId));
    }

    [Fact]
    public void IsModuleEnabled_missing_section_false_for_non_core_module()
    {
        var root = new ConfigNode();
        Assert.False(_sut.IsModuleEnabled(root, "Ape.Module.Example"));
    }

    [Fact]
    public void IsModuleEnabled_null_root_core_still_enabled()
    {
        Assert.True(_sut.IsModuleEnabled(null, LoggingModuleIds.ModuleId));
    }

    [Fact]
    public void IsModuleEnabled_null_root_optional_disabled()
    {
        Assert.False(_sut.IsModuleEnabled(null, "Ape.Module.Example"));
    }

    [Fact]
    public void IsModuleEnabled_empty_object_true()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        modules.GetObject("Ape.Core.Logging");
        Assert.True(_sut.IsModuleEnabled(root, "Ape.Core.Logging"));
    }

    [Fact]
    public void IsModuleEnabled_explicit_false()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var log = modules.GetObject("Ape.Core.Logging");
        log.SetBool("enabled", false);
        Assert.False(_sut.IsModuleEnabled(root, "Ape.Core.Logging"));
    }

    [Fact]
    public void MergePluginNames_modules_only_sorted_and_resolves_short_key()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var m = modules.GetObject("Ape.Module.Z");
        var plugins = m.GetObject("plugins");
        plugins.GetObject("P1");

        var m2 = modules.GetObject("Ape.Module.A");
        var plugins2 = m2.GetObject("plugins");
        plugins2.GetObject("Zed");

        var names = _sut.MergePluginNames(root);
        Assert.Equal(new[] { "Ape.Module.A.Plugin.Zed", "Ape.Module.Z.Plugin.P1" }, names);
    }

    [Fact]
    public void MergePluginNames_skips_module_disabled()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var m = modules.GetObject("Ape.Module.X");
        m.SetBool("enabled", false);
        var plugins = m.GetObject("plugins");
        plugins.GetObject("P");

        Assert.Empty(_sut.MergePluginNames(root));
    }

    [Fact]
    public void MergePluginNames_skips_plugin_disabled()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var m = modules.GetObject("Ape.Module.X");
        var plugins = m.GetObject("plugins");
        var p = plugins.GetObject("P");
        p.SetBool("enabled", false);

        Assert.Empty(_sut.MergePluginNames(root));
    }

    [Fact]
    public void MergePluginNames_ignores_root_plugins_array()
    {
        var root = new ConfigNode();
        root.SetArray("plugins", new List<string> { "Legacy.Plugin.Name" });
        Assert.Empty(_sut.MergePluginNames(root));
    }

    [Fact]
    public void MergePluggableServiceDllNames_respects_module_and_entry_enabled()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var m = modules.GetObject("Ape.Module.X");
        var svc = m.GetObject("services");
        svc.GetObject("Ape.Module.Example");

        var names = _sut.MergePluggableServiceDllNames(root);
        Assert.Contains("Ape.Module.Example", names);
    }

    [Fact]
    public void MergePluggableServiceDllNames_skips_disabled_service_entry()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var m = modules.GetObject("Ape.Module.X");
        var svc = m.GetObject("services");
        var node = svc.GetObject("Ape.Module.Example");
        node.SetBool("enabled", false);

        Assert.Empty(_sut.MergePluggableServiceDllNames(root));
    }

    [Fact]
    public void MergePluggableServiceDllNames_resolves_short_key_to_module_primary_dll()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var a = modules.GetObject("Ape.Module.Alpha");
        a.GetObject("services").GetObject("Alpha");
        var b = modules.GetObject("Ape.Module.Beta");
        b.GetObject("services").GetObject("Beta");

        var names = _sut.MergePluggableServiceDllNames(root);
        Assert.Equal(
            new[] { "Ape.Module.Alpha", "Ape.Module.Beta" },
            names);
    }

    [Fact]
    public void MergePluggableServiceDllNames_short_key_non_matching_suffix_uses_module_dot_key()
    {
        var root = new ConfigNode();
        var modules = root.GetObject("modules");
        var m = modules.GetObject("Ape.Module.Example");
        m.GetObject("services").GetObject("Extra");

        var names = _sut.MergePluggableServiceDllNames(root);
        Assert.Equal(new[] { "Ape.Module.Example.Extra" }, names);
    }

    [Fact]
    public void MergePluggableServiceDllNames_ignores_root_pluggable_array()
    {
        var root = new ConfigNode();
        root.SetArray("pluggableModuleServiceDlls", new List<string> { "Ape.Module.Example" });
        Assert.Empty(_sut.MergePluggableServiceDllNames(root));
    }
}
