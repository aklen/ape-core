using System.Net.Quic;
using System.Runtime.Versioning;
using Ape.Core.Logging;
using Ape.Core.Network.Services;
using Ape.Core.Network.Transport;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class TransportKeepAliveTests
{
    [Fact]
    public void A_configured_interval_reaches_LiteNet_through_the_manager()
    {
        using var transport = new LiteNetLibTransport(new QuietLog());
        var defaultPing = transport.PingIntervalMilliseconds;
        var defaultDisconnect = transport.DisconnectTimeoutMilliseconds;
        var manager = new NetworkManager(transport);

        manager.SetKeepAlive(TimeSpan.FromSeconds(15));

        Assert.Equal(15_000, transport.PingIntervalMilliseconds);
        Assert.Equal(60_000, transport.DisconnectTimeoutMilliseconds);

        manager.SetKeepAlive(TimeSpan.FromSeconds(1));

        Assert.Equal(1_000, transport.PingIntervalMilliseconds);
        Assert.Equal(defaultDisconnect, transport.DisconnectTimeoutMilliseconds);

        manager.SetKeepAlive(TimeSpan.Zero);

        Assert.Equal(defaultPing, transport.PingIntervalMilliseconds);
        Assert.Equal(defaultDisconnect, transport.DisconnectTimeoutMilliseconds);
    }

    [Fact]
    public void Quic_keeps_the_idle_timer_longer_than_the_probe()
    {
        Assert.True(QuicConnection.IsSupported);
        using var transport = new QuicTransport(new QuietLog());
        transport.SetKeepAlive(TimeSpan.FromSeconds(15));
        var quiet = transport.QuicTimers();
        Assert.Equal(TimeSpan.FromSeconds(15), quiet.Probe);
        Assert.Equal(TimeSpan.FromMinutes(2), quiet.Idle);

        transport.SetKeepAlive(TimeSpan.FromMinutes(3));
        var longer = transport.QuicTimers();
        Assert.Equal(TimeSpan.FromMinutes(3), longer.Probe);
        Assert.Equal(TimeSpan.FromMinutes(12), longer.Idle);

        transport.SetKeepAlive(TimeSpan.Zero);
        var defaults = transport.QuicTimers();
        Assert.Equal(Timeout.InfiniteTimeSpan, defaults.Probe);
        Assert.Equal(TimeSpan.FromMinutes(2), defaults.Idle);
    }

    private sealed class QuietLog : ILogger
    {
        public void LogDebug(string message) { }
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
