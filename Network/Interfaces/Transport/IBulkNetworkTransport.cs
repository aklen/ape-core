namespace Ape.Core.Network.Transport;

/// <summary>
/// Optional bounded bulk plane. QUIC implements this on a persistent stream separate
/// from reliable scene state so a large chunk cannot head-of-line block state writes.
/// </summary>
public interface IBulkNetworkTransport
{
    bool TrySendBulk(string peerId, byte[] data);
}
