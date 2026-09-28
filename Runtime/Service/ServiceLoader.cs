using Ape.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.Loader;

namespace Ape.Core.Runtime.Service;

/// <summary>
/// Service loader that loads infrastructure services from DLL files using AssemblyLoadContext.
/// Services provide reusable functionality to plugins (e.g., DeviceManager, HttpApi).
/// Unlike plugins, services don't run in separate threads - they're registered in DI container.
/// </summary>
public class ServiceLoader
{
    private readonly IServiceProvider _services;
    private readonly IServiceCollection _serviceCollection;
    private readonly ILogger _logger;
    private readonly List<ServiceInstance> _loadedServices = new();

    public ServiceLoader(IServiceProvider services, IServiceCollection serviceCollection, ILogger logger)
    {
        _services = services;
        _serviceCollection = serviceCollection;
        _logger = logger;
    }

    /// <summary>
    /// Load a service from a DLL file and initialize it.
    /// </summary>
    public IService? LoadService(string dllPath)
    {
        try
        {
            if (!File.Exists(dllPath))
            {
                _logger.LogError($"Service DLL not found: {dllPath}");
                return null;
            }

            var context = new ServiceLoadContext(dllPath);
            var assembly = context.LoadFromAssemblyPath(dllPath);

            // Find types implementing IService
            var serviceTypes = assembly.GetTypes()
                .Where(t => typeof(IService).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .ToList();

            if (serviceTypes.Count == 0)
            {
                _logger.LogWarning($"No service types found in {dllPath}");
                return null;
            }

            if (serviceTypes.Count > 1)
            {
                _logger.LogWarning($"Multiple service types found in {dllPath}, using first one");
            }

            var serviceType = serviceTypes[0];
            var service = (IService?)Activator.CreateInstance(serviceType);

            if (service == null)
            {
                _logger.LogError($"Failed to create instance of {serviceType.Name}");
                return null;
            }

            // Register service interfaces in DI (before ServiceProvider is built)
            service.Register(_serviceCollection);

            // Initialize service (after ServiceProvider is built)
            service.Initialize(_services);

            var instance = new ServiceInstance
            {
                Service = service,
                LoadContext = context,
                AssemblyPath = dllPath
            };

            _loadedServices.Add(instance);

            _logger.LogInfo($"✅ Loaded service: {service.Name} ({service.ServiceId}) from {Path.GetFileName(dllPath)}");

            return service;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading service from {dllPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load services from config (by name, without .dll extension).
    /// </summary>
    public List<IService> LoadServicesFromConfig(string directory, string[] serviceNames)
    {
        var services = new List<IService>();

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning($"Service directory not found: {directory}");
            return services;
        }

        foreach (var serviceName in serviceNames)
        {
            var dllPath = Path.Combine(directory, $"{serviceName}.dll");

            if (!File.Exists(dllPath))
            {
                _logger.LogWarning($"Service not found: {serviceName} (expected at {dllPath})");
                continue;
            }

            var service = LoadService(dllPath);
            if (service != null)
            {
                services.Add(service);
            }
        }

        return services;
    }

    /// <summary>
    /// Start all loaded services.
    /// </summary>
    public void StartAll(CancellationToken cancellationToken)
    {
        _logger.LogInfo($"Starting {_loadedServices.Count} service(s)...");

        foreach (var instance in _loadedServices)
        {
            try
            {
                instance.Service.Start(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Service {instance.Service.Name} start failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stop all services and cleanup.
    /// </summary>
    public void StopAll()
    {
        _logger.LogInfo("Stopping services...");

        foreach (var instance in _loadedServices)
        {
            try
            {
                instance.Service.Stop();
                instance.LoadContext.Unload();
                _logger.LogInfo($"Stopped service: {instance.Service.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping service {instance.Service.Name}: {ex.Message}");
            }
        }

        _loadedServices.Clear();
    }

    /// <summary>
    /// Get all loaded services.
    /// </summary>
    public List<IService> GetLoadedServices()
    {
        return _loadedServices.Select(i => i.Service).ToList();
    }

    private class ServiceInstance
    {
        public required IService Service { get; init; }
        public required ServiceLoadContext LoadContext { get; init; }
        public required string AssemblyPath { get; init; }
    }
}

/// <summary>
/// Custom AssemblyLoadContext for service isolation.
/// Allows unloading of service assemblies during shutdown.
/// </summary>
internal class ServiceLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ServiceLoadContext(string servicePath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(servicePath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Let shared assemblies be loaded by default context
        var sharedAssemblies = new[]
        {
            "Ape.Core",
            "MessagePack",
            "MessagePack.Annotations",
            "Microsoft.Extensions.DependencyInjection"
        };

        if (sharedAssemblies.Any(name => assemblyName.Name?.StartsWith(name) == true))
        {
            return null; // Use default context
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }
}
