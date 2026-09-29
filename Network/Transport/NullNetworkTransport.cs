#pragma warning disable CS0067 // INetworkTransport events are never raised on this stub

namespace Ape.Core.Network.Transport;

/// <summary>
/// No-op transport: no sockets, no peers. Used when networking is disabled in config
/// but <see cref="Services.NetworkManager"/> is still registered as a core service.
/// <see cref="LocalPeerId"/> uses the same <see cref="PeerIdGenerator"/> shape as <see cref="LiteNetLibTransport"/> (host name, hw hash, PID, random suffix) so logs and ownership strings match the networked case.
/// </summary>
public sealed class NullNetworkTransport : INetworkTransport
{
    /// <summary>Stable for this process instance (matches LiteNetLib peer id semantics).</summary>
    private readonly string _localPeerId = PeerIdGenerator.Generate(Environment.MachineName, "peer", transportId: 0);

    public string TransportType => "none";

    /// <inheritdoc />
    public string LocalPeerId => _localPeerId;

    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<string, byte[], int>? OnDataReceived;
    public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
    public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
    public event Action<string, Guid, int>? OnFileChunkRequested;

    public void StartServer(int port, string? peerName = null, string? role = null) { }

    public void Connect(string host, int port, string? peerName = null, string? role = null) { }

    public void SendReliable(string peerId, byte[] data) { }

    public void SendUnreliable(string peerId, byte[] data) { }

    public void PollEvents() { }

    public void Stop() { }

    public IEnumerable<string> GetConnectedPeers() => Array.Empty<string>();

    public void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data) { }

    public void RequestFileManifest(string peerId, Guid fileId) { }

    public void RequestFileChunk(string peerId, Guid fileId, int chunkIndex) { }

    public void Dispose() { }
}
