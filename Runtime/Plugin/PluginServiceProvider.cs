using Ape.Core.Config;
using Ape.Core.Config.Models;
using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;

namespace Ape.Core.Runtime.Plugin;

/// <summary>
/// Plugin DI uses an explicit Core capability allowlist and permits trusted module-service interfaces.
/// Container, Scene-write, and host-tick capabilities are denied.
/// <para>
/// Types from assemblies named <c>Ape.Module.*</c> match a prefix, not a loader attestation.
/// Trust assumes the host only loads configured module DLLs from <c>plugins/</c> and <c>services/</c>.
/// </para>
/// Core services keep the inner provider (applicator, host loop).
/// </summary>
public sealed class PluginServiceProvider : IServiceProvider
{
    private static readonly HashSet<Type> AllowedCoreTypes =
    [
        typeof(ISceneRead),
        typeof(IFrameParticipantRegistry),
        typeof(ILogger),
        typeof(IEventManager),
        typeof(IConfigManager),
        typeof(IModuleTable),
        typeof(IStartupConfig),
        typeof(INetworkManager),
        typeof(IReplicaManager),
    ];

    private readonly IServiceProvider _inner;
    private ISceneRead? _readFacade;
    private IFrameParticipantRegistry? _registryFacade;

    public PluginServiceProvider(IServiceProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (TryGetEnumerableElementType(serviceType, out var elementType))
            return GetEnumerable(elementType);

        if (serviceType == typeof(ISceneRead))
            return GetReadFacade();

        if (serviceType == typeof(IFrameParticipantRegistry))
            return GetRegistryFacade();

        if (!IsAllowedForPlugins(serviceType))
            return null;

        var instance = _inner.GetService(serviceType);
        if (instance is null)
            return null;
        if (LeaksWriteOrTick(instance) || instance is IServiceProvider)
            return null;
        return instance;
    }

    public static bool IsAllowedForPlugins(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (TryGetEnumerableElementType(serviceType, out var elementType))
            return IsAllowedForPlugins(elementType);
        if (IsContainerEscape(serviceType))
            return false;
        if (IsWriteOrTickType(serviceType))
            return false;
        if (AllowedCoreTypes.Contains(serviceType))
            return true;
        return IsTrustedModuleAssembly(serviceType.Assembly.GetName().Name);
    }

    /// <summary>
    /// Assembly <c>Ape.Module.*</c> is a name prefix, not proof the type came from a vetted module.
    /// Trust assumes the host only loads assemblies from configured <c>plugins/</c> and <c>services/</c> directories.
    /// Arbitrary or unsigned modules with that prefix would also resolve; those need an explicit plugin-visible marker.
    /// </summary>
    public static bool IsTrustedModuleAssembly(string? assemblyName) =>
        assemblyName != null && assemblyName.StartsWith("Ape.Module.", StringComparison.Ordinal);

    public static bool IsHiddenFromPlugins(Type serviceType) => !IsAllowedForPlugins(serviceType);

    private object GetEnumerable(Type elementType)
    {
        if (!IsAllowedForPlugins(elementType))
            return Array.CreateInstance(elementType, 0);

        if (elementType == typeof(ISceneRead))
        {
            var facade = GetReadFacade();
            return facade is null ? Array.Empty<ISceneRead>() : new ISceneRead[] { facade };
        }

        if (elementType == typeof(IFrameParticipantRegistry))
        {
            var facade = GetRegistryFacade();
            return facade is null
                ? Array.Empty<IFrameParticipantRegistry>()
                : new IFrameParticipantRegistry[] { facade };
        }

        var raw = _inner.GetService(typeof(IEnumerable<>).MakeGenericType(elementType));
        if (raw is not IEnumerable enumerable)
            return Array.CreateInstance(elementType, 0);

        var kept = new List<object>();
        foreach (var item in enumerable)
        {
            if (item is null || LeaksWriteOrTick(item) || item is IServiceProvider)
                continue;
            kept.Add(item);
        }

        var array = Array.CreateInstance(elementType, kept.Count);
        for (var i = 0; i < kept.Count; i++)
            array.SetValue(kept[i], i);
        return array;
    }

    private ISceneRead? GetReadFacade()
    {
        if (_readFacade is not null)
            return _readFacade;
        if (_inner.GetService(typeof(ISceneRead)) is not ISceneRead read)
            return null;
        _readFacade = new SceneReadFacade(read);
        return _readFacade;
    }

    private IFrameParticipantRegistry? GetRegistryFacade()
    {
        if (_registryFacade is not null)
            return _registryFacade;
        if (_inner.GetService(typeof(IFrameParticipantRegistry)) is not IFrameParticipantRegistry registry)
            return null;
        _registryFacade = new FrameParticipantRegistryFacade(registry);
        return _registryFacade;
    }

    private static bool IsContainerEscape(Type serviceType) =>
        serviceType == typeof(IServiceProvider)
        || serviceType == typeof(IServiceScopeFactory)
        || serviceType == typeof(IServiceScope)
        || serviceType == typeof(ISupportRequiredService)
        || serviceType == typeof(IKeyedServiceProvider)
        || serviceType == typeof(IServiceProviderIsService)
        || typeof(IServiceProvider).IsAssignableFrom(serviceType);

    private static bool IsWriteOrTickType(Type serviceType) =>
        serviceType == typeof(ISceneManager)
        || typeof(ISceneManager).IsAssignableFrom(serviceType)
        || serviceType == typeof(ISceneCommitSink)
        || typeof(ISceneCommitSink).IsAssignableFrom(serviceType)
        || serviceType == typeof(IHostFrameRunner)
        || serviceType == typeof(IDeterministicHostTick)
        || serviceType == typeof(SceneCommitService);

    private static bool LeaksWriteOrTick(object instance) =>
        instance is ISceneManager
            or ISceneCommitSink
            or IHostFrameRunner
            or IDeterministicHostTick
            or SceneCommitService;

    private static bool TryGetEnumerableElementType(Type serviceType, out Type elementType)
    {
        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = serviceType.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }
}
