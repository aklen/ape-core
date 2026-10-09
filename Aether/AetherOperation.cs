using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

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
        var stored = new Dictionary<string, FieldValue>(changes, StringComparer.Ordinal);
        Changes = new ReadOnlyDictionary<string, FieldValue>(stored);
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
        using var buffer = new MemoryStream();
        WriteString(buffer, entityId);
        WriteString(buffer, writerId);
        WriteString(buffer, actorId);
        WriteInt64(buffer, sequence);
        WriteInt64(buffer, lamport);
        foreach (var key in changes.Keys.Order(StringComparer.Ordinal))
        {
            var value = changes[key];
            WriteString(buffer, key);
            if (value.Text is null)
            {
                buffer.WriteByte(0);
                WriteInt64(buffer, value.Fixed);
            }
            else
            {
                buffer.WriteByte(1);
                WriteInt64(buffer, value.Fixed);
                WriteString(buffer, value.Text);
            }
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteString(Stream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }

    private static void WriteInt64(Stream buffer, long value)
    {
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, value);
        buffer.Write(encoded);
    }
}
