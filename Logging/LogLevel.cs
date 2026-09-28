namespace Ape.Core.Logging;

/// <summary>
/// Minimum log severity for filtering (config key <c>level</c> under <c>Ape.Core.Logging</c>).
/// </summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Parses config strings such as <c>debug</c>, <c>info</c>, <c>warning</c>, <c>error</c>.
/// </summary>
public static class LogLevelParsing
{
    public static LogLevel ParseOrDefault(string? s, LogLevel defaultLevel = LogLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultLevel;

        return s.Trim().ToLowerInvariant() switch
        {
            "trace" or "debug" or "verbose" => LogLevel.Debug,
            "info" or "information" => LogLevel.Info,
            "warn" or "warning" => LogLevel.Warning,
            "error" or "fatal" => LogLevel.Error,
            _ => defaultLevel,
        };
    }
}
