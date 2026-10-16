using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Ape.Core.Logging;
using Ape.Core.Network.Services;
using Ape.Core.Network.Transport;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class QuicPeerReconnectTests
{
    [Fact]
    public async Task A_listener_accepts_a_peer_that_dials_out()
    {
        QuietLog.Lines.Clear();
        Assert.True(QuicConnection.IsSupported);
        var port = FreeUdpPort();
        using var serverTransport = new QuicTransport(new QuietLog());
        using var peerTransport = new QuicTransport(new QuietLog());
        var server = new NetworkManager(serverTransport);
        var peer = new NetworkManager(peerTransport);
        peer.SetReconnect(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));

        server.StartServer(port, "server", "server");
        peer.StartServer(FreeUdpPort(), "peer", "peer");
        peer.Track("127.0.0.1", port);

        await WaitUntil(() => server.GetConnectedPeers().Count > 0 && peer.GetConnectedPeers().Count > 0, server, peer);

        peer.Stop();
        server.Stop();
    }

    [Fact]
    public async Task A_client_dials_again_after_the_listener_returns()
    {
        QuietLog.Lines.Clear();
        Assert.True(QuicConnection.IsSupported);
        var port = FreeUdpPort();
        using var serverTransport = new QuicTransport(new QuietLog());
        using var clientTransport = new QuicTransport(new QuietLog());
        var server = new NetworkManager(serverTransport);
        var client = new NetworkManager(clientTransport);
        client.SetReconnect(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));

        server.StartServer(port, "server", "server");
        client.Connect("127.0.0.1", port, "client", "client");
        await WaitUntil(() => client.GetConnectedPeers().Count > 0 && server.GetConnectedPeers().Count > 0, client, server);

        server.Stop();
        await WaitUntil(() => client.GetConnectedPeers().Count == 0, client);

        using var returnedTransport = new QuicTransport(new QuietLog());
        var returned = new NetworkManager(returnedTransport);
        returned.StartServer(port, "server", "server");
        await WaitUntil(() => client.GetConnectedPeers().Count > 0 && returned.GetConnectedPeers().Count > 0, client, returned);

        client.Stop();
        returned.Stop();
    }

    private static async Task WaitUntil(Func<bool> ready, params NetworkManager[] managers)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var manager in managers)
                manager.PollEvents();
            if (ready())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for the QUIC session. " + string.Join(" | ", QuietLog.Lines));
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class QuietLog : ILogger
    {
        public static List<string> Lines { get; } = [];

        public void LogDebug(string message) => Lines.Add(message);

        public void LogInfo(string message) => Lines.Add(message);

        public void LogWarning(string message) => Lines.Add(message);

        public void LogError(string message, Exception? ex = null) => Lines.Add(message);
    }
}
