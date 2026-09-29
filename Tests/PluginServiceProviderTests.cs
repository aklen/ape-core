using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Runtime.Service;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class PluginServiceProviderTests
{
    [Fact]
    public void Plugin_scope_allows_reads_and_participant_registry()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        Assert.NotNull(plugins.GetService(typeof(ISceneRead)));
        var registry = plugins.GetService(typeof(IFrameParticipantRegistry));
        Assert.NotNull(registry);
        Assert.False(registry is IHostFrameRunner);
        Assert.False(registry is IDeterministicHostTick);
        Assert.IsNotType<SceneCommitService>(registry);
        Assert.Same(registry, plugins.GetService(typeof(IFrameParticipantRegistry)));
    }

    [Fact]
    public void Plugin_scope_ISceneRead_is_not_the_write_manager()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        var read = plugins.GetService(typeof(ISceneRead));
        Assert.NotNull(read);
        Assert.False(read is ISceneManager);
        Assert.IsNotType<SceneManager>(read);
        Assert.NotSame(root.GetService(typeof(ISceneRead)), read);
        Assert.Same(read, plugins.GetService(typeof(ISceneRead)));
    }

    [Fact]
    public void Plugin_scope_hides_write_and_tick_surfaces()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        Assert.NotNull(root.GetService(typeof(ISceneManager)));
        Assert.Null(plugins.GetService(typeof(ISceneManager)));
        Assert.Null(plugins.GetService(typeof(SceneManager)));
        Assert.Null(plugins.GetService(typeof(ISceneCommitSink)));
        Assert.Null(plugins.GetService(typeof(IFrameCommitBatch)));
        Assert.Null(plugins.GetService(typeof(IHostFrameRunner)));
        Assert.Null(plugins.GetService(typeof(IDeterministicHostTick)));
        Assert.Null(plugins.GetService(typeof(SceneCommitService)));
    }

    [Fact]
    public void Plugin_scope_allows_core_capabilities()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        Assert.NotNull(plugins.GetService(typeof(ILogger)));
        Assert.NotNull(plugins.GetService(typeof(IEventManager)));
        Assert.NotNull(plugins.GetService(typeof(INetworkManager)));
        Assert.NotNull(plugins.GetService(typeof(IReplicaManager)));
    }

    [Fact]
    public void Plugin_scope_hides_container_escape()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        Assert.NotNull(root.GetService(typeof(IServiceProvider)));
        Assert.Null(plugins.GetService(typeof(IServiceProvider)));
        Assert.Null(plugins.GetService(typeof(IServiceScopeFactory)));
        Assert.Null(plugins.GetService(typeof(IServiceScope)));
        Assert.Null(plugins.GetService(typeof(ISupportRequiredService)));
        Assert.Null(plugins.GetService(typeof(IKeyedServiceProvider)));

        var providers = plugins.GetService(typeof(IEnumerable<IServiceProvider>)) as IEnumerable<IServiceProvider>;
        Assert.NotNull(providers);
        Assert.Empty(providers);
    }

    [Fact]
    public void Plugin_scope_does_not_enumerate_write_types()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        var managers = plugins.GetService(typeof(IEnumerable<ISceneManager>)) as IEnumerable<ISceneManager>;
        Assert.NotNull(managers);
        Assert.Empty(managers);

        var sinks = plugins.GetService(typeof(IEnumerable<ISceneCommitSink>)) as IEnumerable<ISceneCommitSink>;
        Assert.NotNull(sinks);
        Assert.Empty(sinks);

        var reads = plugins.GetService(typeof(IEnumerable<ISceneRead>)) as IEnumerable<ISceneRead>;
        Assert.NotNull(reads);
        Assert.Single(reads);
        Assert.False(reads.Single() is ISceneManager);
    }

    [Fact]
    public void Plugin_scope_strips_write_manager_from_core_service_enumerable()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);

        var cores = plugins.GetService(typeof(IEnumerable<ICoreService>)) as IEnumerable<ICoreService>;
        Assert.NotNull(cores);
        Assert.Empty(cores);
    }

    [Fact]
    public void Plugin_scope_trusts_module_assemblies_not_core_wrappers()
    {
        Assert.True(PluginServiceProvider.IsTrustedModuleAssembly("Ape.Module.DeviceManager"));
        Assert.False(PluginServiceProvider.IsTrustedModuleAssembly("Ape.Core"));
        Assert.False(PluginServiceProvider.IsTrustedModuleAssembly("Ape.Core.Tests"));
        Assert.False(PluginServiceProvider.IsAllowedForPlugins(typeof(IModuleSceneEscape)));
        Assert.False(PluginServiceProvider.IsAllowedForPlugins(typeof(PluginServiceProviderTests)));
    }

    [Fact]
    public void Plugin_scope_denies_core_defined_wrappers_that_could_leak_scene()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var plugins = new PluginServiceProvider(root);
        Assert.Null(plugins.GetService(typeof(IModuleSceneEscape)));
        Assert.Null(plugins.GetService(typeof(ISceneManager)));
    }

    [Fact]
    public void Core_applicator_still_resolves_write_capable_scene()
    {
        var root = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        Assert.NotNull(root.GetRequiredService<ISceneManager>());
        Assert.NotNull(root.GetRequiredService<SceneCommitService>());
        Assert.True(root.GetRequiredService<ISceneRead>() is ISceneManager);
    }
}

/// <summary>
/// Stand-in for a module service that wraps <see cref="ISceneManager"/>.
/// Defined in Ape.Core.Tests so plugin DI denies it; the same type in Ape.Module.* would resolve (trusted).
/// </summary>
internal interface IModuleSceneEscape
{
    ISceneManager Scene { get; }
}
