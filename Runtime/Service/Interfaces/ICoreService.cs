namespace Ape.Core.Runtime.Service;

/// <summary>
/// Interface for core infrastructure services that are always loaded.
/// Core services provide essential functionality required by the system:
///   - ILogger: Logging infrastructure
///   - IEventManager: Event system
///   - IConfigManager: Configuration management
///   - INetworkManager: Network communication
///   - IReplicaManager: Object replication
///   - ISceneManager: Scene graph management
///
/// The host registers core services first in the bootstrap sequence; module <see cref="IPluggableService"/>
/// implementations may run later in that same sequence where dependencies require it, then plugins load.
/// They follow the same Register() → Initialize() → Start() → Stop() lifecycle as pluggable services.
/// </summary>
public interface ICoreService : IService
{
    // Inherits all IService members:
    // - string ServiceId { get; }
    // - string Name { get; }
    // - void Register(IServiceCollection serviceCollection);
    // - void Initialize(IServiceProvider services);
    // - void Start(CancellationToken cancellationToken);
    // - void Stop();
}
