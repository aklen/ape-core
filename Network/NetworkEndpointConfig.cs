using Ape.Core.Config.Models;

namespace Ape.Core.Network;

/// <summary>
/// Reads static peer addresses and reconnect timing from an <c>Ape.Core.Network</c> section.
/// </summary>
public static class NetworkEndpointConfig
{
    public static readonly TimeSpan DefaultInitial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultMax = TimeSpan.FromSeconds(8);

    public static IReadOnlyList<(string Host, int Port)> Peers(IConfigNode? section, int defaultPort)
    {
        var peers = new List<(string Host, int Port)>();
        if (section == null || !section.HasKey("peers"))
            return peers;

        foreach (var peer in section.GetArray("peers"))
        {
            var host = peer.GetString("host").Trim();
            if (host.Length == 0)
                continue;

            var port = peer.HasKey("port") ? peer.GetInt("port") : defaultPort;
            if (port is > 0 and <= 65535)
                peers.Add((host, port));
        }

        return peers;
    }

    public static (TimeSpan Initial, TimeSpan Max) Reconnect(IConfigNode? section)
    {
        if (section == null || !section.TryGetChildObject("reconnect", out var reconnect))
            return (DefaultInitial, DefaultMax);

        var initialSeconds = reconnect.GetInt("initialSeconds", (int)DefaultInitial.TotalSeconds);
        var maxSeconds = reconnect.GetInt("maxSeconds", (int)DefaultMax.TotalSeconds);
        if (initialSeconds <= 0)
            initialSeconds = (int)DefaultInitial.TotalSeconds;
        if (maxSeconds < initialSeconds)
            maxSeconds = initialSeconds;

        return (TimeSpan.FromSeconds(initialSeconds), TimeSpan.FromSeconds(maxSeconds));
    }
}
