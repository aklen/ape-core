using Ape.Core.Runtime.Service;
using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Logging.Services;
using Ape.Core.Network;
using Ape.Core.Network.Transport;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Ape.Core.Network.Services;

/// <summary>
/// Network manager that delegates to pluggable transport implementations.
/// Supports LiteNetLib (UDP) and QUIC (.NET 9+) transports.
/// Also implements ICoreService for direct registration in the DI container.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class NetworkManager : INetworkManager, IPrincipalSigner, ICoreService, IDisposable
{
    public string ServiceId => "core-network-manager";
    public string Name => "Network Manager";

    private ILogger? _logger;
    private IEventManager _eventManager = null!;      // Set in Initialize()
    private readonly INetworkTransport _transport;
    private readonly object _peersGate = new();
    private readonly Dictionary<string, PeerConnectionContext> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Handshake> _handshakes = new(StringComparer.Ordinal);
    private readonly DurablePrincipal? _profile;
    private readonly DurablePrincipal? _signer;
    private readonly TimeProvider _time;
    private readonly bool _allowUnauthenticated;
    private string _role = String.Empty;

    public event Action<string, byte[]>? OnDataReceived;
    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<PeerConnectionContext>? OnPeerIdentified;

    /// <summary>
    /// Local peer ID for this instance.
    /// Unique identifier (GUID or transport-assigned). Used for replica ownership tracking.
    /// </summary>
    public string LocalPeerId => _transport.LocalPeerId;

    /// <summary>
    /// Durable writer id. A profile key sends its public-key fingerprint. <see cref="LocalPeerId"/> stays the process instance.
    /// A checked handshake proves key presence. Signed lifecycle operations retain authorship; reducer rules grant authority.
    /// </summary>
    public string PrincipalId { get; }

    bool IPrincipalSigner.HasPrincipalSigner => _signer is not null;

    bool IPrincipalSigner.TrySignPrincipal(ReadOnlySpan<byte> payload, out byte[] publicKey, out byte[] signature)
    {
        if (_signer is null)
        {
            publicKey = [];
            signature = [];
            return false;
        }

        publicKey = _signer.PublicKey.ToArray();
        signature = _signer.Sign(payload);
        return true;
    }

    public bool TrySendBulk(string connectionId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _transport.TrySendBulk(connectionId, data);
    }

    /// <summary>
    /// Network role of this instance ("server", "client", "peer").
    /// </summary>
    public string Role => _role.ToString();

    /// <summary>
    /// Constructor for NetworkManager.
    /// Transport must be created at construction time (not in Initialize).
    /// Logger will be resolved from DI in Initialize().
    /// </summary>
    public NetworkManager(string transportType = "quic", string? profileDirectory = null)
    {
        // Temporary logger until DI provides the real one
        var tempLogger = new LogManager("console");

        // Factory pattern for transport selection ("none" = no sockets; use when network.enabled is false)
        var transport = transportType.ToLowerInvariant() switch
        {
            "quic" => new QuicTransport(tempLogger),
            "litenetlib" => (INetworkTransport)new LiteNetLibTransport(tempLogger),
            "none" or "noop" or "empty" => new NullNetworkTransport(),
            _ => throw new ArgumentException($"Unknown transport type: {transportType}")
        };
        _transport = transport;
        _time = TimeProvider.System;
        if (string.IsNullOrEmpty(profileDirectory))
            PrincipalId = Convert.ToHexString(RandomNumberGenerator.GetBytes(DurablePrincipal.ByteLength)).ToLowerInvariant();
        else
        {
            _profile = DurablePrincipal.Open(profileDirectory);
            _signer = _profile;
            PrincipalId = _profile.Id;
        }

        Wire();

        tempLogger.LogDebug($"NetworkManager initialized with {transportType} transport");
    }

    internal NetworkManager(INetworkTransport transport, string? principalId = null, bool allowUnauthenticated = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _time = TimeProvider.System;
        _allowUnauthenticated = allowUnauthenticated;
        PrincipalId = string.IsNullOrEmpty(principalId) ? transport.LocalPeerId : principalId;
        Wire();
    }

    internal NetworkManager(INetworkTransport transport, DurablePrincipal signer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(signer);
        _transport = transport;
        _signer = signer;
        _time = time ?? TimeProvider.System;
        PrincipalId = signer.Id;
        Wire();
    }

    private void Wire()
    {
        _transport.OnDataReceived += (connectionId, data, length) =>
        {
            if (length >= 1 && data[0] == PrincipalHandshake.Marker)
            {
                if (length < 2 || data[1] != PrincipalHandshake.Version || length > PrincipalHandshake.MaxBytes)
                    return;
                var packet = new byte[length];
                Array.Copy(data, packet, length);
                AcceptHandshake(connectionId, packet);
                return;
            }

            if (length >= 2 && PeerHello.HasMarker(data.AsSpan(0, length)))
            {
                if (length > PeerHello.MaxBytes || _signer is not null || !_allowUnauthenticated)
                    return;
                var hello = new byte[length];
                Array.Copy(data, hello, length);
                if (PeerHello.TryDecode(hello, out var principal))
                    Bind(connectionId, principal, authenticated: false);
                return;
            }

            var buffer = new byte[length];
            Array.Copy(data, buffer, length);
            OnDataReceived?.Invoke(connectionId, buffer);
        };
        _transport.OnPeerConnected += connectionId =>
        {
            try
            {
                if (_signer is not null)
                    SendHello(connectionId);
                else if (_allowUnauthenticated)
                    _transport.SendReliable(connectionId, PeerHello.Encode(PrincipalId));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[NetworkManager] Peer hello was not sent: {ex.Message}");
            }

            OnPeerConnected?.Invoke(connectionId);
        };
        _transport.OnPeerDisconnected += connectionId =>
        {
            lock (_peersGate)
            {
                _peers.Remove(connectionId);
                _handshakes.Remove(connectionId);
            }

            OnPeerDisconnected?.Invoke(connectionId);
        };
    }

    private void SendHello(string connectionId)
    {
        lock (_peersGate)
        {
            if (!_handshakes.TryGetValue(connectionId, out var state))
            {
                state = new Handshake();
                _handshakes[connectionId] = state;
            }

            if (state.Failed)
                return;
            if (state.LocalNonce.Length != PrincipalHandshake.NonceLength)
            {
                state.LocalNonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
                state.StartedAt = _time.GetUtcNow();
            }

            state.HelloEnvelope ??= PrincipalHandshake.EncodeHello(_signer!, state.LocalNonce);
        }

        Flush(connectionId);
    }

    private void Flush(string connectionId)
    {
        Handshake? state;
        lock (_peersGate)
        {
            if (!_handshakes.TryGetValue(connectionId, out state))
                return;
            if (state.Failed || Expired(state))
            {
                Fail(state);
                return;
            }
        }

        if (!SendEnvelope(connectionId, state, hello: true))
            return;
        SendProofIfReady(connectionId, state);
    }

    private void AcceptHandshake(string connectionId, byte[] packet)
    {
        if (_signer is null || !PrincipalHandshake.TryRead(packet, out var message))
            return;
        if (message.Kind == 1)
            AcceptHello(connectionId, message);
        else
            AcceptProof(connectionId, message);
    }

    private void AcceptHello(string connectionId, PrincipalHandshake.Message message)
    {
        Handshake state;
        lock (_peersGate)
        {
            if (_peers.ContainsKey(connectionId))
                return;
            if (!_handshakes.TryGetValue(connectionId, out state!))
            {
                state = new Handshake { StartedAt = _time.GetUtcNow() };
                _handshakes[connectionId] = state;
            }

            if (state.Failed || Expired(state))
            {
                Fail(state);
                return;
            }

            if (state.RemoteNonce is not null)
                return;
            if (!DurablePrincipal.IsCanonicalP256(message.PublicKey))
            {
                Fail(state);
                return;
            }

            var fingerprint = SHA256.HashData(message.PublicKey);
            if (message.Fingerprint.Length != fingerprint.Length
                || !CryptographicOperations.FixedTimeEquals(message.Fingerprint, fingerprint)
                || message.Fingerprint.AsSpan().SequenceEqual(_signer!.FingerprintBytes))
            {
                Fail(state);
                return;
            }

            state.RemoteNonce = message.Nonce;
            state.RemoteKey = message.PublicKey;
            state.RemoteFingerprint = message.Fingerprint;
        }

        Flush(connectionId);
    }

    private void AcceptProof(string connectionId, PrincipalHandshake.Message message)
    {
        string? principal = null;
        lock (_peersGate)
        {
            if (_peers.ContainsKey(connectionId) || !_handshakes.TryGetValue(connectionId, out var state))
                return;
            if (state.Failed || Expired(state) || state.RemoteNonce is null || state.LocalNonce.Length != PrincipalHandshake.NonceLength)
            {
                Fail(state);
                return;
            }

            var transcript = PrincipalHandshake.Transcript(
                state.RemoteFingerprint!,
                _signer!.FingerprintBytes,
                state.RemoteNonce,
                state.LocalNonce,
                state.RemoteKey!,
                _signer.PublicKey);
            if (!DurablePrincipal.Verify(state.RemoteKey!, transcript, message.Signature))
            {
                Fail(state);
                return;
            }

            principal = Convert.ToHexString(state.RemoteFingerprint!).ToLowerInvariant();
        }

        if (principal is not null && !Bind(connectionId, principal, authenticated: true))
        {
            lock (_peersGate)
            {
                if (_handshakes.TryGetValue(connectionId, out var state))
                    Fail(state);
            }
        }
    }

    private void SendProofIfReady(string connectionId, Handshake state)
    {
        lock (_peersGate)
        {
            if (state.Failed || state.ProofSent || state.RemoteNonce is null || state.LocalNonce.Length != PrincipalHandshake.NonceLength)
                return;
            if (state.ProofEnvelope is null)
            {
                var transcript = PrincipalHandshake.Transcript(
                    _signer!.FingerprintBytes,
                    state.RemoteFingerprint!,
                    state.LocalNonce,
                    state.RemoteNonce,
                    _signer.PublicKey,
                    state.RemoteKey!);
                state.ProofEnvelope = PrincipalHandshake.EncodeProof(_signer.Sign(transcript));
            }
        }

        SendEnvelope(connectionId, state, hello: false);
    }

    private bool SendEnvelope(string connectionId, Handshake state, bool hello)
    {
        byte[]? envelope;
        lock (_peersGate)
        {
            if (state.Failed)
                return false;
            if (hello ? state.HelloSent : state.ProofSent)
                return true;
            envelope = hello ? state.HelloEnvelope : state.ProofEnvelope;
            if (envelope is null)
                return false;
        }

        try
        {
            _transport.SendReliable(connectionId, envelope);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[NetworkManager] Peer hello was not sent: {ex.Message}");
            return false;
        }

        lock (_peersGate)
        {
            if (hello)
                state.HelloSent = true;
            else
                state.ProofSent = true;
        }

        return true;
    }

    private bool Expired(Handshake state) => _time.GetUtcNow() - state.StartedAt > PrincipalHandshake.Lifetime;

    private static void Fail(Handshake state)
    {
        state.Failed = true;
        state.RemoteNonce = null;
        state.RemoteKey = null;
        state.RemoteFingerprint = null;
    }

    private sealed class Handshake
    {
        public byte[] LocalNonce { get; set; } = [];
        public byte[]? RemoteNonce { get; set; }
        public byte[]? RemoteKey { get; set; }
        public byte[]? RemoteFingerprint { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public bool Failed { get; set; }
        public byte[]? HelloEnvelope { get; set; }
        public byte[]? ProofEnvelope { get; set; }
        public bool HelloSent { get; set; }
        public bool ProofSent { get; set; }
    }

    public bool TryGetPeer(string connectionId, out PeerConnectionContext peer)
    {
        lock (_peersGate)
            return _peers.TryGetValue(connectionId, out peer);
    }

    private bool Bind(string connectionId, string principal, bool authenticated)
    {
        PeerConnectionContext bound;
        lock (_peersGate)
        {
            if (_peers.ContainsKey(connectionId))
                return false;
            if (_handshakes.TryGetValue(connectionId, out var handshake) && handshake.Failed)
                return false;
            foreach (var peer in _peers.Values)
            {
                if (string.Equals(peer.PrincipalId, principal, StringComparison.Ordinal))
                    return false;
            }

            bound = new PeerConnectionContext(connectionId, principal, authenticated);
            _peers[connectionId] = bound;
        }

        OnPeerIdentified?.Invoke(bound);
        return true;
    }

    // ICoreService implementation
    public void Register(IServiceCollection serviceCollection)
    {
        // Register this instance as the INetworkManager singleton
        serviceCollection.AddSingleton<INetworkManager>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        // Get services from DI (replaces temporary logger)
        _eventManager = services.GetRequiredService<IEventManager>();
        _logger = services.GetRequiredService<ILogger>();
        _logger.LogDebug($"NetworkManager initialized with logger from DI");
    }

    public void Start(CancellationToken cancellationToken)
    {
        // Network manager starts with the core engine
    }

    // Note: Stop() is already implemented as part of INetworkManager

    // INetworkManager implementation
    public void StartServer(int port, string? peerName = null, string? role = null)
    {
        _role = role ?? "server";
        _transport.StartServer(port, peerName, role);
    }

    public void Connect(string address, int port, string? peerName = null, string? role = null)
    {
        _role = role ?? "client";
        _transport.Connect(address, port, peerName, role);
    }

    public void SetKeepAlive(TimeSpan interval) => _transport.SetKeepAlive(interval);

    public void Stop()
    {
        _transport.Stop();
        lock (_peersGate)
            _peers.Clear();
    }

    public void Dispose()
    {
        _profile?.Dispose();
    }

    public void Broadcast(byte[] data, bool reliable = true)
    {
        foreach (var peerId in _transport.GetConnectedPeers())
        {
            Send(peerId, data, reliable);
        }
    }

    public void Send(string peerId, byte[] data, bool reliable = true)
    {
        Send(peerId, data, reliable, null);
    }

    public void Send(string peerId, byte[] data, bool reliable, Action<bool>? onFlushed)
    {
        if (reliable)
        {
            _transport.SendReliable(peerId, data, onFlushed);
            return;
        }

        _transport.SendUnreliable(peerId, data);
        onFlushed?.Invoke(true);
    }

    public void PollEvents()
    {
        _transport.PollEvents();
        string[] pending;
        lock (_peersGate)
        {
            foreach (var handshake in _handshakes.Values)
            {
                if (!handshake.Failed && Expired(handshake))
                    Fail(handshake);
            }

            pending = _handshakes
                .Where(pair => !pair.Value.Failed && (!pair.Value.HelloSent || (pair.Value.ProofEnvelope is not null && !pair.Value.ProofSent)))
                .Select(pair => pair.Key)
                .ToArray();
        }

        foreach (var connectionId in pending)
            Flush(connectionId);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetConnectedPeers()
    {
        return _transport.GetConnectedPeers().ToList();
    }

    // IP Address Discovery Methods

    /// <inheritdoc/>
    public IEnumerable<string> GetLocalIPAddresses()
    {
        var addresses = new List<string>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var ipProperties = ni.GetIPProperties();
                var ipAddresses = ipProperties.UnicastAddresses
                    .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(ua => !IPAddress.IsLoopback(ua.Address))
                    .Where(ua => !IsLinkLocal(ua.Address))
                    .Select(ua => ua.Address.ToString());

                addresses.AddRange(ipAddresses);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Failed to enumerate local IP addresses: {ex.Message}");
        }

        return addresses;
    }

    /// <inheritdoc/>
    public string GetPreferredLocalIPAddress()
    {
        try
        {
            // Strategy 1: Get IP by connecting to external address (doesn't actually connect)
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // Google DNS
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            if (endPoint != null)
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            // Fallback to strategy 2
        }

        try
        {
            // Strategy 2: Get first active non-loopback interface
            var addresses = GetLocalIPAddresses().ToList();
            if (addresses.Any())
            {
                return addresses.First();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Failed to get preferred IP address: {ex.Message}");
        }

        // Fallback to localhost
        _logger?.LogWarning("No network interface found, using 127.0.0.1");
        return "127.0.0.1";
    }

    /// <summary>
    /// Check if IP address is link-local (169.254.x.x).
    /// </summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
