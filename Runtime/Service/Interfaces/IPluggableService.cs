namespace Ape.Core.Runtime.Service;

/// <summary>
/// Optional infrastructure services: module-bundled (registered in-process by the host) and/or
/// extra assemblies loaded from <c>services/*.dll</c> when listed in config.
/// Examples: scene entity registration in <c>Ape.Module.*</c>, HTTP APIs, database access.
///
/// The host builds a single ordered bootstrap list: all <see cref="ICoreService"/> registrations first,
/// then interleaved module pluggables where required (e.g. scene factories before <c>SceneManager</c>),
/// then optional DLL services. Initialization and startup follow that same order, before plugins.
/// They follow the same Register() → Initialize() → Start() → Stop() lifecycle as <see cref="ICoreService"/>.
/// </summary>
public interface IPluggableService : IService
{
    // Inherits all IService members:
    // - string ServiceId { get; }
    // - string Name { get; }
    // - void Register(IServiceCollection serviceCollection);
    // - void Initialize(IServiceProvider services);
    // - void Start(CancellationToken cancellationToken);
    // - void Stop();
}
