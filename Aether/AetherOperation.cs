using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Ape.Core.Aether;

public enum AetherEffect : byte
{
    Write = 0,
    DeleteEntity = 1,
    HideShared = 2,
    RestoreShared = 3,
    WithdrawPublication = 4,
    Publish = 5,
    CreateEntity = 6,
    TransferOwnership = 7,
}

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
        IReadOnlyDictionary<string, FieldValue> changes,
        AetherEffect effect = AetherEffect.Write,
        string? publicationId = null,
        string? entityTypeId = null,
        string? creatorId = null,
        string? ownerId = null,
        string? newOwnerId = null,
        string? priorOwnerOperationId = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        EntityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        WriterId = writerId ?? throw new ArgumentNullException(nameof(writerId));
        ActorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
        Sequence = sequence;
        Lamport = lamport;
        var stored = new Dictionary<string, FieldValue>(changes, StringComparer.Ordinal);
        if (effect == AetherEffect.Write)
        {
            if (publicationId is not null)
                throw new AetherProtocolException("A field write has no publication.");
        }
        else if (stored.Count > 0)
        {
            throw new AetherProtocolException("A lifecycle operation has no field values.");
        }

        if (effect is AetherEffect.Publish or AetherEffect.WithdrawPublication)
        {
            if (string.IsNullOrEmpty(publicationId))
                throw new AetherProtocolException("Membership needs a publication id.");
        }
        else if (publicationId is not null)
        {
            throw new AetherProtocolException("This operation has no publication.");
        }

        if (effect == AetherEffect.CreateEntity)
        {
            if (string.IsNullOrEmpty(entityTypeId) || string.IsNullOrEmpty(creatorId) || string.IsNullOrEmpty(ownerId))
                throw new AetherProtocolException("Create needs a type, creator, and owner.");
        }
        else if (entityTypeId is not null || creatorId is not null || ownerId is not null)
        {
            throw new AetherProtocolException("This operation has no creation metadata.");
        }

        if (effect == AetherEffect.TransferOwnership)
        {
            if (string.IsNullOrEmpty(newOwnerId) || string.IsNullOrEmpty(priorOwnerOperationId))
                throw new AetherProtocolException("Ownership transfer needs a new owner and prior operation.");
        }
        else if (newOwnerId is not null || priorOwnerOperationId is not null)
        {
            throw new AetherProtocolException("This operation has no ownership metadata.");
        }

        Effect = effect;
        PublicationId = publicationId;
        EntityTypeId = entityTypeId;
        CreatorId = creatorId;
        OwnerId = ownerId;
        NewOwnerId = newOwnerId;
        PriorOwnerOperationId = priorOwnerOperationId;
        Changes = new ReadOnlyDictionary<string, FieldValue>(stored);
        Digest = ComputeDigest(EntityId, WriterId, ActorId, Sequence, Lamport, Changes, effect, publicationId, entityTypeId, creatorId, ownerId, newOwnerId, priorOwnerOperationId);
    }

    public string Id { get; }

    public string EntityId { get; }

    public string WriterId { get; }

    public string ActorId { get; }

    public long Sequence { get; }

    public long Lamport { get; }

    public AetherEffect Effect { get; }

    public string? PublicationId { get; }

    public string? EntityTypeId { get; }

    public string? CreatorId { get; }

    public string? OwnerId { get; }
    public string? NewOwnerId { get; }
    public string? PriorOwnerOperationId { get; }

    public IReadOnlyDictionary<string, FieldValue> Changes { get; }

    public string Digest { get; }

    public static string ComputeDigest(
        string entityId,
        string writerId,
        string actorId,
        long sequence,
        long lamport,
        IReadOnlyDictionary<string, FieldValue> changes,
        AetherEffect effect = AetherEffect.Write,
        string? publicationId = null,
        string? entityTypeId = null,
        string? creatorId = null,
        string? ownerId = null,
        string? newOwnerId = null,
        string? priorOwnerOperationId = null)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)effect);
        WriteString(buffer, publicationId ?? "");
        WriteString(buffer, entityTypeId ?? "");
        WriteString(buffer, creatorId ?? "");
        WriteString(buffer, ownerId ?? "");
        WriteString(buffer, newOwnerId ?? "");
        WriteString(buffer, priorOwnerOperationId ?? "");
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
