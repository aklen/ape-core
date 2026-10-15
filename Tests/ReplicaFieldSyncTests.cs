using System.Runtime.Versioning;
using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Replication.Services;
using Ape.Core.Scene;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class ReplicaFieldSyncTests
{
    [Fact]
    public void An_update_carries_only_the_changed_field_and_names_the_replica()
    {
        var source = new FieldProbe { Id = "device-1", OwnerId = "owner" };
        var target = new FieldProbe { Id = "device-1", OwnerId = "owner" };
        using (SceneMutationScope.Begin())
        {
            source.Location = "http://192.0.2.10/device.xml";
            source.Present = true;
            target.Location = "old";
            target.Present = true;
        }

        var packet = ReplicaPacketBuilder.BuildPropertyUpdate(
            source,
            new[] { new ReplicaFieldSample(nameof(FieldProbe.Location), source.Location) });

        Assert.True(ReplicaPacketBuilder.TryParsePropertyUpdate(packet, out var id, out var owner, out var fields));
        Assert.Equal("device-1", id);
        Assert.Equal("owner", owner);
        Assert.Equal(nameof(FieldProbe.Location), Assert.Single(fields).Name);

        using (SceneMutationScope.Begin())
            target.ApplySyncField(fields[0].Name, fields[0].Encoded);

        Assert.Equal(source.Location, target.Location);
        Assert.True(target.Present);
    }

    [Fact]
    public void A_failed_flush_keeps_that_peers_fields_dirty()
    {
        var probe = new FieldProbe { Id = "device-1", OwnerId = "owner" };
        using (SceneMutationScope.Begin())
        {
            probe.Location = "http://192.0.2.10/device.xml";
            probe.Present = true;
        }

        var outbound = new ReplicaFieldOutbound();
        var sent = new List<(string Peer, byte Type)>();
        outbound.Pump(probe, new[] { "ok", "lost" }, ReplicaPacketType.Create, (peer, packet, done) =>
        {
            sent.Add((peer, packet[0]));
            done(peer == "ok");
        });

        Assert.Equal(2, sent.Count);
        Assert.All(sent, item => Assert.Equal((byte)ReplicaPacketType.Create, item.Type));

        sent.Clear();
        using (SceneMutationScope.Begin())
            probe.Location = "http://192.0.2.11/device.xml";

        outbound.Pump(probe, new[] { "ok", "lost" }, ReplicaPacketType.FullSync, (peer, packet, done) =>
        {
            sent.Add((peer, packet[0]));
            Assert.Equal(peer == "ok" ? (byte)ReplicaPacketType.Update : (byte)ReplicaPacketType.Create, packet[0]);
            if (packet[0] == (byte)ReplicaPacketType.Update)
            {
                Assert.True(ReplicaPacketBuilder.TryParsePropertyUpdate(packet, out _, out _, out var fields));
                Assert.Equal(nameof(FieldProbe.Location), Assert.Single(fields).Name);
            }

            done(true);
        });

        Assert.Contains(sent, item => item.Peer == "ok" && item.Type == (byte)ReplicaPacketType.Update);
        Assert.Contains(sent, item => item.Peer == "lost" && item.Type == (byte)ReplicaPacketType.Create);
    }

    [Fact]
    public void An_update_for_an_unknown_replica_does_not_create_one()
    {
        var manager = new ReplicaManager();
        var network = new QuietNetwork();
        manager.Initialize(new ServiceCollection()
            .AddSingleton<INetworkManager>(network)
            .AddSingleton<IEventManager>(new QuietEvents())
            .AddSingleton<ILogger>(new QuietLog())
            .BuildServiceProvider());

        var source = new FieldProbe { Id = "missing", OwnerId = "owner" };
        using (SceneMutationScope.Begin())
            source.Location = "http://192.0.2.10/device.xml";
        var packet = ReplicaPacketBuilder.BuildPropertyUpdate(
            source,
            new[] { new ReplicaFieldSample(nameof(FieldProbe.Location), source.Location) });

        manager.OnNetworkData("peer", packet);
        Assert.Null(manager.GetReplica("missing"));
    }

    [MessagePackObject]
    internal sealed class FieldProbe : Replica
    {
        private string _location = "";
        private bool _present;

        [Key(1)]
        public string Location
        {
            get => _location;
            set => SetProperty(ref _location, value);
        }

        [Key(2)]
        public bool Present
        {
            get => _present;
            set => SetProperty(ref _present, value);
        }
    }

    private sealed class QuietNetwork : INetworkManager
    {
        public string LocalPeerId => "owner";
        public string PrincipalId => "owner";
        public string Role => "server";
        public event Action<string, byte[]>? OnDataReceived;
        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<PeerConnectionContext>? OnPeerIdentified;
        public void StartServer(int port, string? peerName = null, string? role = null) { }
        public void Connect(string address, int port, string? peerName = null, string? role = null) { }
        public void Stop() { }
        public void Broadcast(byte[] data, bool reliable = true) { }
        public void Send(string peerId, byte[] data, bool reliable = true) { }
        public bool TrySendBulk(string connectionId, byte[] data) => false;
        public void PollEvents() { }
        public bool TryGetPeer(string connectionId, out PeerConnectionContext peer)
        {
            peer = default;
            return false;
        }
        public IReadOnlyList<string> GetConnectedPeers() => Array.Empty<string>();
        public IEnumerable<string> GetLocalIPAddresses() => Array.Empty<string>();
        public string GetPreferredLocalIPAddress() => "127.0.0.1";
    }

    private sealed class QuietEvents : IEventManager
    {
        public void Subscribe<T>(string pluginId, Action<T> handler) where T : IEvent { }
        public void Unsubscribe<T>(string pluginId) where T : IEvent { }
        public void Publish(IEvent evt) { }
        public void DrainEventsFor(string pluginId) { }
    }

    private sealed class QuietLog : ILogger
    {
        public void LogDebug(string message) { }
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
