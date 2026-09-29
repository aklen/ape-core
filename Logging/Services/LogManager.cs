using Ape.Core.Config;
using Ape.Core.Config.Models;
using Ape.Core.Runtime.Service;
using Ape.Core.Logging;
using Ape.Core.Logging.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Logging.Services;

/// <summary>
/// Log manager with pluggable transport support.
/// Implements ICoreService for direct registration in the DI container.
///
/// Transport types:
///   - "console": Write logs to console with color coding (default)
///   - "none": Discard all log output
///   - "file": Write logs to file
///   - "syslog": Send logs to syslog server (future)
///
/// Minimum level: read from <c>modules["Ape.Core.Logging"].level</c>
/// during <see cref="Initialize"/> (default <see cref="LogLevel.Info"/>).
/// </summary>
public class LogManager : ILogger, ICoreService
{
    public string ServiceId => "core-logger";
    public string Name => "Log Manager";

    private readonly ILogTransport _transport;
    private LogLevel _minLevel = LogLevel.Info;

    /// <summary>
    /// Create LogManager with specified transport type.
    /// </summary>
    /// <param name="transportType">Transport type: "console", "none", "file", "console+file", etc.</param>
    public LogManager(string transportType = "console")
    {
        _transport = transportType.ToLowerInvariant() switch
        {
            "none" => new NullLogTransport(),
            "console" => new ConsoleLogTransport(),
            "file" => new FileLogTransport("logs/apecore.log"),
            "console+file" => new CompositeLogTransport(
                new ConsoleLogTransport(),
                new FileLogTransport("logs/apecore.log")
            ),
            // Future transports: "syslog" => new SyslogTransport(),
            _ => throw new ArgumentException($"Unknown log transport type: {transportType}", nameof(transportType))
        };
    }

    // ========================================================================
    // ICoreService implementation
    // ========================================================================

    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<ILogger>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        var startup = services.GetService<IStartupConfig>();
        var moduleTable = services.GetRequiredService<IModuleTable>();
        var moduleSection = moduleTable.GetModuleSection(startup?.Root, LoggingModuleIds.ModuleId);
        var levelStr = moduleSection?.GetString("level");
        _minLevel = LogLevelParsing.ParseOrDefault(levelStr, LogLevel.Info);
    }

    public void Start(CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
    }

    // ========================================================================
    // ILogger implementation - delegates to transport
    // ========================================================================

    public void LogDebug(string message)
    {
        if (_minLevel > LogLevel.Debug)
            return;
        _transport.LogDebug(message);
    }

    public void LogInfo(string message)
    {
        if (_minLevel > LogLevel.Info)
            return;
        _transport.LogInfo(message);
    }

    public void LogWarning(string message)
    {
        if (_minLevel > LogLevel.Warning)
            return;
        _transport.LogWarning(message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        if (_minLevel > LogLevel.Error)
            return;
        _transport.LogError(message, ex);
    }
}
