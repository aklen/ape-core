namespace Ape.Core.Network.Transport;

/// <summary>
/// Maps one configured interval onto a transport's native timers.
/// A zero interval means that transport keeps its own defaults.
/// The drop timer stays at least four probes long so one late probe does not close the connection.
/// </summary>
internal static class TransportKeepAlive
{
    private const int MissedProbesBeforeDrop = 4;

    public static TimeSpan QuicProbe(TimeSpan interval) =>
        interval > TimeSpan.Zero ? interval : Timeout.InfiniteTimeSpan;

    public static TimeSpan QuicIdle(TimeSpan interval, TimeSpan floor)
    {
        if (interval <= TimeSpan.Zero)
            return floor;

        var scaledTicks = (double)interval.Ticks * MissedProbesBeforeDrop;
        var scaled = TimeSpan.FromTicks((long)Math.Min(scaledTicks, TimeSpan.MaxValue.Ticks));
        return scaled > floor ? scaled : floor;
    }

    public static int LiteNetPingMilliseconds(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return (int)Math.Clamp(interval.TotalMilliseconds, 1, int.MaxValue);
    }

    public static int LiteNetDisconnectMilliseconds(TimeSpan interval, int floorMilliseconds)
    {
        var scaled = (long)LiteNetPingMilliseconds(interval) * MissedProbesBeforeDrop;
        var clamped = (int)Math.Min(scaled, int.MaxValue);
        return Math.Max(floorMilliseconds, clamped);
    }
}
