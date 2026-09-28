using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Runtime.Service;

/// <summary>
/// Base interface for infrastructure services.
/// Services provide reusable functionality to plugins (e.g., HTTP APIs, databases).
/// Unlike plugins, services don't run in separate threads - they're registered in DI and provide APIs.
///
/// Lifecycle:
///   1. Register() - Register service interfaces in DI container (BEFORE ServiceProvider is built)
///   2. Initialize() - Initialize service with dependencies (AFTER ServiceProvider is built)
///   3. Start() - Optional: Start background tasks (e.g., polling, monitoring)
///   4. Stop() - Optional: Cleanup and stop background tasks
/// 
/// Example:
///   public class MyService : IService
///   {
///       private MyServiceImpl? _implementation;
///       
///       public void Register(IServiceCollection serviceCollection)
///       {
///           // Register interface (implementation created later in Initialize)
///           serviceCollection.AddSingleton&lt;IMyService&gt;(sp => _implementation!);
///       }
///       
///       public void Initialize(IServiceProvider services)
///       {
///           var logger = services.GetService&lt;ILogger&gt;();
///           _implementation = new MyServiceImpl(logger);
///       }
///       public void Start(CancellationToken cancellationToken)
///       {
///           // Start background tasks
///       }
///       public void Stop()
///       {
///           // Stop background tasks
///       }
///   }
/// </summary>
public interface IService
{
    /// <summary>
    /// Unique service identifier (e.g., "http-api").
    /// Used for logging and identification.
    /// </summary>
    string ServiceId { get; }

    /// <summary>
    /// Human-readable service name (e.g., "HTTP API").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Register service interfaces in DI container.
    /// Called during application startup, BEFORE ServiceProvider is built.
    /// This allows plugins to resolve service interfaces registered here.
    /// </summary>
    /// <param name="serviceCollection">Service collection for registering this service's interface</param>
    void Register(IServiceCollection serviceCollection);

    /// <summary>
    /// Initialize the service with dependencies from the ServiceProvider.
    /// Called during application startup, AFTER ServiceProvider is built, before plugins are loaded.
    /// </summary>
    /// <param name="services">Service provider for accessing other services (ILogger, ISceneManager, etc.)</param>
    void Initialize(IServiceProvider services);

    /// <summary>
    /// Optional: Start background tasks (e.g., polling, HTTP server listening).
    /// Called after all services and plugins are initialized.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
    void Start(CancellationToken cancellationToken);

    /// <summary>
    /// Optional: Stop background tasks and cleanup resources.
    /// Called during application shutdown.
    /// </summary>
    void Stop();
}
