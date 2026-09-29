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

namespace Ape.Core.Network.Services;

/// <summary>
/// Network manager that delegates to pluggable transport implementations.
/// Supports LiteNetLib (UDP) and QUIC (.NET 9+) transports.
/// Also implements ICoreService for direct registration in the DI container.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class NetworkManager : INetworkManager, ICoreService
{
    public string ServiceId => "core-network-manager";
    public string Name => "Network Manager";

    private ILogger? _logger;
    private IEventManager _eventManager = null!;      // Set in Initialize()
    private readonly INetworkTransport _transport;
    private string _role = String.Empty;

    public event Action<string, byte[]>? OnDataReceived;
    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;

    /// <summary>
    /// Local peer ID for this instance.
    /// Unique identifier (GUID or transport-assigned). Used for replica ownership tracking.
    /// </summary>
    public string LocalPeerId => _transport.LocalPeerId;

    /// <summary>
    /// Network role of this instance ("server", "client", "peer").
    /// </summary>
    public string Role => _role.ToString();

    /// <summary>
    /// Constructor for NetworkManager.
    /// Transport must be created at construction time (not in Initialize).
    /// Logger will be resolved from DI in Initialize().
    /// </summary>
    public NetworkManager(string transportType = "litenetlib")
    {
        // Temporary logger until DI provides the real one
        var tempLogger = new LogManager("console");

        // Factory pattern for transport selection ("none" = no sockets; use when network.enabled is false)
        _transport = transportType.ToLowerInvariant() switch
        {
            "quic" => new QuicTransport(tempLogger),
            "litenetlib" => new LiteNetLibTransport(tempLogger),
            "none" or "noop" or "empty" => new NullNetworkTransport(),
            _ => throw new ArgumentException($"Unknown transport type: {transportType}")
        };

        // Forward events from transport
        _transport.OnDataReceived += (peerId, data, length) =>
        {
            var buffer = new byte[length];
            Array.Copy(data, buffer, length);
            OnDataReceived?.Invoke(peerId, buffer);
        };

        _transport.OnPeerConnected += peerId => OnPeerConnected?.Invoke(peerId);
        _transport.OnPeerDisconnected += peerId => OnPeerDisconnected?.Invoke(peerId);

        tempLogger.LogDebug($"NetworkManager initialized with {transportType} transport");
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

    public void Stop()
    {
        _transport.Stop();
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
        if (reliable)
        {
            _transport.SendReliable(peerId, data);
        }
        else
        {
            _transport.SendUnreliable(peerId, data);
        }
    }

    public void PollEvents()
    {
        _transport.PollEvents();
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
