namespace Ape.Core.Network;

internal interface IBulkNetworkSender
{
    bool TrySendBulk(string connectionId, byte[] data);
}
