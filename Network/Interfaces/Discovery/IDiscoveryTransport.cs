namespace Ape.Core.Network.Discovery;

/// <summary>
/// Interface for network discovery transport implementations.
/// Abstracts the underlying discovery protocol (e.g., SSDP, mDNS).
/// </summary>
public interface IDiscoveryTransport : IDisposable
{
    /// <summary>
    /// Type of the transport (e.g., "udp", "quic", "webrtc").
    /// </summary>
    string TransportType { get; }

    /// <summary>
    /// Starts the discovery transport, enabling advertisement and/or discovery.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the discovery transport, disabling advertisement and/or discovery.
    /// </summary>
    void Stop();

    /// <summary>
    /// Discovers devices on the network.
    /// </summary>
    /// <returns>A collection of discovered device descriptions.</returns>
    Task<IEnumerable<string>> DiscoverDevicesAsync();

    /// <summary>
    /// Advertises the current instance on the network.
    /// </summary>
    /// <param name="deviceDescription">The description of the device to advertise.</param>
    void Advertise(string deviceDescription);

    /// <summary>
    /// Triggered when one or more devices are discovered on the network.
    /// </summary>
    event Action<IEnumerable<string>> DevicesDiscovered;

    /// <summary>
    /// Triggered when a device disappears from the network.
    /// </summary>
    event Action<string> DeviceLost;
}
