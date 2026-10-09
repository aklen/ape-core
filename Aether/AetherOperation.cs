namespace Ape.Core.Aether;

/// <summary>
/// One admitted write. <see cref="Lamport"/> stays as stamped.
/// <see cref="WriterId"/> is the author. The first slice has no relay, so the carrier is the same peer.
/// </summary>
public sealed class AetherOperation
{
    public AetherOperation(
        string id,
        string entityId,
        string writerId,
        string actorId,
        long sequence,
        long lamport,
        IReadOnlyDictionary<string, FieldValue> changes)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        EntityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        WriterId = writerId ?? throw new ArgumentNullException(nameof(writerId));
        ActorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
        Sequence = sequence;
        Lamport = lamport;
        Changes = new Dictionary<string, FieldValue>(changes, StringComparer.Ordinal);
        Digest = ComputeDigest(EntityId, WriterId, ActorId, Sequence, Lamport, Changes);
    }

    public string Id { get; }

    public string EntityId { get; }

    public string WriterId { get; }

    public string ActorId { get; }

    public long Sequence { get; }

    public long Lamport { get; }

    public IReadOnlyDictionary<string, FieldValue> Changes { get; }

    public string Digest { get; }

    public static string ComputeDigest(
        string entityId,
        string writerId,
        string actorId,
        long sequence,
        long lamport,
        IReadOnlyDictionary<string, FieldValue> changes)
    {
        var parts = new List<string>
        {
            entityId,
            writerId,
            actorId,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lamport.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var key in changes.Keys.Order(StringComparer.Ordinal))
        {
            var value = changes[key];
            parts.Add(key);
            parts.Add(value.Fixed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(value.Text ?? "");
        }

        return string.Join("|", parts);
    }
}
