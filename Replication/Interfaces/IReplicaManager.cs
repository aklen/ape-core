namespace Ape.Core.Replication;

/// <summary>
/// Manages the lifecycle and synchronization of Replica instances across the network.
/// </summary>
public interface IReplicaManager
{
    void Register(IReplica replica);

    void Unregister(string replicaId);

    IReplica? GetReplica(string replicaId);

    void Tick();

    void OnNetworkData(string peerId, byte[] data);

    string GetLocalPeerId();

    void Subscribe(string pathPattern);

    void Unsubscribe(string pathPattern);
}
