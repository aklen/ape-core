using Ape.Core.Config.Models;
using Ape.Core.Network;
using Ape.Core.Network.Services;
using Ape.Core.Network.Transport;

namespace Ape.Core.Tests;

public class PeerSessionTests
{
    [Fact]
    public void A_tracked_endpoint_dials_once_until_it_fails()
    {
        var time = new ManualTime();
        var transport = new DialTransport();
        var manager = new NetworkManager(transport, time: time);
        manager.SetReconnect(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));

        manager.Track("10.0.0.2", 9050);
        manager.Track("10.0.0.2", 9050);

        Assert.Equal([("10.0.0.2", 9050)], transport.Dials);

        transport.Fail("10.0.0.2", 9050);
        manager.PollEvents();
        Assert.Single(transport.Dials);

        time.Advance(TimeSpan.FromSeconds(1));
        manager.PollEvents();
        Assert.Equal(2, transport.Dials.Count);

        transport.Fail("10.0.0.2", 9050);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.PollEvents();
        Assert.Equal(2, transport.Dials.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        manager.PollEvents();
        Assert.Equal(3, transport.Dials.Count);
    }

    [Fact]
    public void A_live_endpoint_is_not_redialed_until_it_drops()
    {
        var time = new ManualTime();
        var transport = new DialTransport();
        var manager = new NetworkManager(transport, time: time);
        manager.SetReconnect(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

        manager.Connect("10.0.0.3", 19151, "client", "client");
        transport.Succeed("slot-1", "10.0.0.3", 19151);
        time.Advance(TimeSpan.FromMinutes(1));
        manager.PollEvents();

        Assert.Single(transport.Dials);
        Assert.Equal("client", manager.Role);

        transport.Drop("slot-1");
        manager.PollEvents();
        Assert.Single(transport.Dials);

        time.Advance(TimeSpan.FromSeconds(1));
        manager.PollEvents();
        Assert.Equal(2, transport.Dials.Count);
    }

    [Fact]
    public void Untrack_stops_later_attempts()
    {
        var time = new ManualTime();
        var transport = new DialTransport();
        var manager = new NetworkManager(transport, time: time);
        manager.SetReconnect(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

        manager.Track("10.0.0.4", 1);
        manager.Track("10.0.0.5", 2);
        Assert.Equal(2, transport.Dials.Count);

        manager.Untrack("10.0.0.4", 1);
        transport.Fail("10.0.0.4", 1);
        transport.Fail("10.0.0.5", 2);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.PollEvents();

        Assert.Equal(("10.0.0.5", 2), transport.Dials[^1]);
        Assert.Equal(3, transport.Dials.Count);
    }

    [Fact]
    public void Separate_endpoints_keep_their_own_wait()
    {
        var time = new ManualTime();
        var transport = new DialTransport();
        var manager = new NetworkManager(transport, time: time);
        manager.SetReconnect(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));

        manager.Track("10.0.0.2", 9050);
        manager.Track("10.0.0.3", 9050);
        transport.Fail("10.0.0.2", 9050);
        transport.Succeed("slot", "10.0.0.3", 9050);

        time.Advance(TimeSpan.FromSeconds(2));
        manager.PollEvents();

        Assert.Equal(3, transport.Dials.Count);
        Assert.Equal(("10.0.0.2", 9050), transport.Dials[^1]);
    }

    [Fact]
    public void Config_reads_peer_addresses_and_reconnect_bounds()
    {
        var section = new ConfigNode();
        var first = new ConfigNode();
        first.SetString("host", " 10.0.0.2 ");
        first.SetInt("port", 19151);
        var second = new ConfigNode();
        second.SetString("host", "10.0.0.3");
        var blank = new ConfigNode();
        blank.SetString("host", " ");
        section.SetObjectArray("peers", [first, second, blank]);

        var peers = NetworkEndpointConfig.Peers(section, 9050);

        Assert.Equal([("10.0.0.2", 19151), ("10.0.0.3", 9050)], peers);
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)), NetworkEndpointConfig.Reconnect(null));

        var reconnect = section.GetObject("reconnect");
        reconnect.SetInt("initialSeconds", 5);
        reconnect.SetInt("maxSeconds", 2);
        Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), NetworkEndpointConfig.Reconnect(section));
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class DialTransport : INetworkTransport, IOutboundDial
    {
        public List<(string Host, int Port)> Dials { get; } = [];

        public string TransportType => "dial";

        public string LocalPeerId => "local";

        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[], int>? OnDataReceived;
        public event Action<string, string, int>? OutboundConnected;
        public event Action<string, int>? DialFailed;
        public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
        public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
        public event Action<string, Guid, int>? OnFileChunkRequested;

        public void StartOutbound(string? peerName = null, string? role = null)
        {
        }

        public void Dial(string host, int port) => Dials.Add((host, port));

        public void Fail(string host, int port) => DialFailed?.Invoke(host, port);

        public void Succeed(string connectionId, string host, int port)
        {
            OnPeerConnected?.Invoke(connectionId);
            OutboundConnected?.Invoke(connectionId, host, port);
        }

        public void Drop(string connectionId) => OnPeerDisconnected?.Invoke(connectionId);

        public void StartServer(int port, string? peerName = null, string? role = null)
        {
        }

        public void Connect(string host, int port, string? peerName = null, string? role = null) => Dial(host, port);

        public void SendReliable(string peerId, byte[] data)
        {
        }

        public void SendUnreliable(string peerId, byte[] data)
        {
        }

        public bool TrySendBulk(string peerId, byte[] data) => false;

        public void PollEvents()
        {
        }

        public void Stop()
        {
        }

        public IEnumerable<string> GetConnectedPeers() => [];

        public void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data)
        {
        }

        public void RequestFileManifest(string peerId, Guid fileId)
        {
        }

        public void RequestFileChunk(string peerId, Guid fileId, int chunkIndex)
        {
        }

        public void Dispose()
        {
        }
    }
}
