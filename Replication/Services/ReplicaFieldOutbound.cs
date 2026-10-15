using Ape.Core.Replication;

namespace Ape.Core.Replication.Services;

/// <summary>
/// Per-peer memory of which replica image and which property values reached the transport queue.
/// A failed flush leaves those properties dirty for that peer only.
/// </summary>
internal sealed class ReplicaFieldOutbound
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, PeerReplica>> _byReplica = new(StringComparer.Ordinal);

    public void ForgetPeer(string peerId)
    {
        lock (_gate)
        {
            foreach (var peers in _byReplica.Values)
            {
                if (peers.Remove(peerId, out var slot))
                    slot.Retired = true;
            }
        }
    }

    public void ForgetReplica(string replicaId)
    {
        lock (_gate)
        {
            if (!_byReplica.Remove(replicaId, out var peers))
                return;
            foreach (var slot in peers.Values)
                slot.Retired = true;
        }
    }

    public void Pump(
        Replica replica,
        IReadOnlyList<string> peers,
        ReplicaPacketType imageType,
        Action<string, byte[], Action<bool>> send)
    {
        var fields = replica.ReadSyncFields();
        var outgoing = new List<Pending>();
        lock (_gate)
        {
            if (!_byReplica.TryGetValue(replica.Id, out var slots))
            {
                slots = new Dictionary<string, PeerReplica>(StringComparer.Ordinal);
                _byReplica[replica.Id] = slots;
            }

            foreach (var peerId in peers)
            {
                if (!slots.TryGetValue(peerId, out var slot))
                {
                    slot = new PeerReplica();
                    slots[peerId] = slot;
                }

                if (slot.InFlight)
                    continue;

                if (!slot.ImageChosen)
                {
                    slot.ImageChosen = true;
                    slot.ImageType = imageType;
                }

                if (!slot.ImageFlushed)
                {
                    slot.InFlight = true;
                    outgoing.Add(new Pending(peerId, slot, image: true, fields.ToList(),
                        ReplicaPacketBuilder.BuildPacket(slot.ImageType, replica)));
                    continue;
                }

                var dirty = new List<ReplicaFieldSample>();
                foreach (var field in fields)
                {
                    if (!slot.Flushed.TryGetValue(field.Name, out var sent) || !Equals(sent, field.Value))
                        dirty.Add(field);
                }

                if (dirty.Count == 0)
                    continue;

                slot.InFlight = true;
                outgoing.Add(new Pending(peerId, slot, image: false, dirty,
                    ReplicaPacketBuilder.BuildPropertyUpdate(replica, dirty)));
            }
        }

        foreach (var item in outgoing)
        {
            send(item.PeerId, item.Packet, ok => Complete(item, ok));
        }
    }

    private void Complete(Pending item, bool flushed)
    {
        lock (_gate)
        {
            item.Slot.InFlight = false;
            if (!flushed || item.Slot.Retired)
                return;

            foreach (var field in item.Sent)
                item.Slot.Flushed[field.Name] = field.Value;
            if (item.Image)
                item.Slot.ImageFlushed = true;
        }
    }

    private sealed class PeerReplica
    {
        public bool Retired;
        public bool InFlight;
        public bool ImageChosen;
        public bool ImageFlushed;
        public ReplicaPacketType ImageType;
        public Dictionary<string, object?> Flushed { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Pending
    {
        public Pending(string peerId, PeerReplica slot, bool image, List<ReplicaFieldSample> sent, byte[] packet)
        {
            PeerId = peerId;
            Slot = slot;
            Image = image;
            Sent = sent;
            Packet = packet;
        }

        public string PeerId { get; }
        public PeerReplica Slot { get; }
        public bool Image { get; }
        public List<ReplicaFieldSample> Sent { get; }
        public byte[] Packet { get; }
    }
}
