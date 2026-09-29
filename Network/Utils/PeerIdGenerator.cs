using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Ape.Core.Network;

/// <summary>
/// Generates unique peer identifiers for network instances.
/// Format: {peerName}-{role}-{hwHash}-{processId}-{transportId}-{guid}
///
/// Components:
/// - peerName: Human-readable name (from config/CLI, or auto-generated)
/// - role: Network role ("server", "client", "peer")
/// - hwHash: MD5 hash of hardware ID (MAC + CPU) - first 8 chars
/// - processId: Current process ID (multi-instance support)
/// - transportId: Transport connection ID (for multiple transports)
/// - guid: Random GUID (collision prevention) - first 8 chars
/// </summary>
public static class PeerIdGenerator
{
    /// <summary>
    /// Generate a unique PeerId.
    /// </summary>
    /// <param name="peerName">Human-readable name (from config/CLI). If null, auto-generates "unnamed-{guid}"</param>
    /// <param name="role">Network role: "server", "client", or "peer"</param>
    /// <param name="transportId">Transport connection ID (optional, default 0)</param>
    /// <returns>Unique peer identifier string</returns>
    public static string Generate(string? peerName, string role, int transportId = 0)
    {
        var name = SanitizeName(peerName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"unnamed-{Guid.NewGuid().ToString("N")[..4]}";
        }

        var roleStr = role.ToLowerInvariant();
        if (roleStr != "server" && roleStr != "client" && roleStr != "peer")
        {
            throw new ArgumentException($"Invalid role '{role}'. Must be 'server', 'client', or 'peer'.", nameof(role));
        }

        var hwHash = GetHardwareHash();
        var processId = Process.GetCurrentProcess().Id;
        var tidStr = transportId.ToString();
        var guid = Guid.NewGuid().ToString("N")[..8];

        return $"{name}-{roleStr}-{hwHash}-{processId}-{tidStr}-{guid}";
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sanitized = new string(name
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        while (sanitized.Contains("--"))
            sanitized = sanitized.Replace("--", "-");

        sanitized = sanitized.Trim('-');

        if (sanitized.Length > 32)
            sanitized = sanitized[..32];

        return sanitized.ToLowerInvariant();
    }

    private static string GetHardwareHash()
    {
        try
        {
            var macAddress = NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .FirstOrDefault(mac => !string.IsNullOrEmpty(mac));

            if (string.IsNullOrEmpty(macAddress))
            {
                macAddress = "unknown";
            }

            var cpuCount = Environment.ProcessorCount.ToString();
            var hwString = $"{macAddress}-{cpuCount}";

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(hwString));
            return BitConverter.ToString(hash).Replace("-", "")[..8].ToLowerInvariant();
        }
        catch
        {
            return Guid.NewGuid().ToString("N")[..8];
        }
    }

    /// <summary>
    /// Parse a PeerId string into its components.
    /// </summary>
    public static (string peerName, string role, string hwHash, int processId, int transportId, string guid) Parse(string peerId)
    {
        var parts = peerId.Split('-');

        if (parts.Length < 6)
        {
            throw new ArgumentException($"Invalid PeerId format: '{peerId}'. Expected at least 6 components.", nameof(peerId));
        }

        var peerName = string.Join("-", parts.Take(parts.Length - 5));
        var role = parts[^5];
        var hwHash = parts[^4];
        var processId = int.Parse(parts[^3]);
        var transportId = int.Parse(parts[^2]);
        var guid = parts[^1];

        return (peerName, role, hwHash, processId, transportId, guid);
    }

    public static string GetPeerName(string peerId)
    {
        var (peerName, _, _, _, _, _) = Parse(peerId);
        return peerName;
    }

    public static string GetRole(string peerId)
    {
        var (_, role, _, _, _, _) = Parse(peerId);
        return role;
    }
}
