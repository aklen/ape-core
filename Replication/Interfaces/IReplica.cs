using Ape.Core.Event;
using Ape.Core.Network.FileTransfer;

namespace Ape.Core.Replication;

/// <summary>
/// Interface for all replicable objects in ApeCore.
/// Provides serialization, property change detection, and network synchronization.
/// </summary>
public interface IReplica
{
    string Id { get; }

    string UniquePath { get; }

    bool IsLocal { get; set; }

    string OwnerId { get; set; }

    object SyncRoot { get; }

    IEventManager? EventManager { get; set; }

    ILargeFileTransferService? LargeFileTransferService { get; set; }

    byte[] Serialize();

    void Deserialize(ReadOnlyMemory<byte> data);

    void SnapshotProperties();

    bool HasChanges();

    void NotifyPropertyChanged(string propertyName);
}
