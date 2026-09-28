using Ape.Core.Logging;
using Ape.Core.Runtime.Service;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Logging.Services;

/// <summary>
/// Registers a no-op <see cref="ILogger"/> when <c>modules["Ape.Core.Logging"].enabled</c> is <c>false</c>.
/// </summary>
public sealed class NullLoggerService : ILogger, ICoreService
{
    public string ServiceId => "core-logger-null";
    public string Name => "Null Logger";

    public void Register(IServiceCollection serviceCollection) =>
        serviceCollection.AddSingleton<ILogger>(this);

    public void Initialize(IServiceProvider services)
    {
    }

    public void Start(CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
    }

    public void LogDebug(string message)
    {
    }

    public void LogInfo(string message)
    {
    }

    public void LogWarning(string message)
    {
    }

    public void LogError(string message, Exception? ex = null)
    {
    }
}
