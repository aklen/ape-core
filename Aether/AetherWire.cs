using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ape.Core.Network;

namespace Ape.Core.Aether;

/// <summary>
/// Versioned state packet for one label write. File bytes and live frames do not use this packet.
/// </summary>
public static class AetherWire
{
    public const byte PacketType = 0x30;
    public const byte ContributionType = 0x31;
    public const byte CreateType = 0x32;
    public const byte DeleteType = 0x33;
    public const byte TransferType = 0x34;
    public const ushort WireVersion = 2;
    public const string LabelRule = "lww";
    public const int MaxPacketBytes = 65_536;
    public const int MaxFieldCount = 64;
    public const string PacketTooLarge = "State packet is too large.";

    public static bool IsStatePacket(ReadOnlySpan<byte> data) =>
        !data.IsEmpty && (data[0] == PacketType || data[0] == ContributionType || data[0] == CreateType || data[0] == DeleteType || data[0] == TransferType);

    public static bool IsContributionPacket(ReadOnlySpan<byte> data) =>
        !data.IsEmpty && data[0] == ContributionType;

    public static bool IsCreatePacket(ReadOnlySpan<byte> data) =>
        !data.IsEmpty && data[0] == CreateType;

    public static bool IsDeletePacket(ReadOnlySpan<byte> data) =>
        !data.IsEmpty && data[0] == DeleteType;

    public static byte[] Encode(AetherOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (op.Effect == AetherEffect.CreateEntity)
            return EncodeCreate(op);
        if (op.Effect == AetherEffect.DeleteEntity)
            return EncodeDelete(op);
        if (op.Effect == AetherEffect.TransferOwnership)
            return EncodeTransfer(op);
        if (op.Effect != AetherEffect.Write || op.Changes.Count != 1)
            throw new AetherProtocolException("This packet carries one label write.");
        var field = op.Changes.Single();
        if (!field.Value.IsText)
            throw new AetherProtocolException("This packet carries one label write.");

        var size = 0;
        Reserve(ref size, 1 + 2);
        ReserveText(ref size, LabelRule);
        ReserveText(ref size, op.Id);
        ReserveText(ref size, op.EntityId);
        ReserveText(ref size, op.WriterId);
        ReserveText(ref size, op.ActorId);
        Reserve(ref size, 8 + 8);
        ReserveText(ref size, field.Key);
        ReserveText(ref size, field.Value.Text!);

        using var body = new MemoryStream(size);
        body.WriteByte(PacketType);
        WriteU16(body, WireVersion);
        WriteText(body, LabelRule);
        WriteText(body, op.Id);
        WriteText(body, op.EntityId);
        WriteText(body, op.WriterId);
        WriteText(body, op.ActorId);
        WriteI64(body, op.Sequence);
        WriteI64(body, op.Lamport);
        WriteText(body, field.Key);
        WriteText(body, field.Value.Text!);
        return WithChecksum(body.ToArray());
    }

    private static byte[] EncodeCreate(AetherOperation op)
    {
        var size = 0;
        Reserve(ref size, 1 + 2);
        ReserveText(ref size, op.Id);
        ReserveText(ref size, op.EntityId);
        ReserveText(ref size, op.WriterId);
        ReserveText(ref size, op.ActorId);
        Reserve(ref size, 8 + 8);
        ReserveText(ref size, op.EntityTypeId!);
        ReserveText(ref size, op.CreatorId!);
        ReserveText(ref size, op.OwnerId!);
        ReserveProof(ref size, op.Authority);

        using var body = new MemoryStream(size);
        body.WriteByte(CreateType);
        WriteU16(body, WireVersion);
        WriteText(body, op.Id);
        WriteText(body, op.EntityId);
        WriteText(body, op.WriterId);
        WriteText(body, op.ActorId);
        WriteI64(body, op.Sequence);
        WriteI64(body, op.Lamport);
        WriteText(body, op.EntityTypeId!);
        WriteText(body, op.CreatorId!);
        WriteText(body, op.OwnerId!);
        WriteProof(body, op.Authority);
        return WithChecksum(body.ToArray());
    }

    private static byte[] EncodeDelete(AetherOperation op)
    {
        var size = 0;
        Reserve(ref size, 1 + 2);
        ReserveText(ref size, op.Id);
        ReserveText(ref size, op.EntityId);
        ReserveText(ref size, op.WriterId);
        ReserveText(ref size, op.ActorId);
        Reserve(ref size, 8 + 8);
        ReserveProof(ref size, op.Authority);

        using var body = new MemoryStream(size);
        body.WriteByte(DeleteType);
        WriteU16(body, WireVersion);
        WriteText(body, op.Id);
        WriteText(body, op.EntityId);
        WriteText(body, op.WriterId);
        WriteText(body, op.ActorId);
        WriteI64(body, op.Sequence);
        WriteI64(body, op.Lamport);
        WriteProof(body, op.Authority);
        return WithChecksum(body.ToArray());
    }

    private static byte[] EncodeTransfer(AetherOperation op)
    {
        var size = 0;
        Reserve(ref size, 1 + 2);
        ReserveText(ref size, op.Id);
        ReserveText(ref size, op.EntityId);
        ReserveText(ref size, op.WriterId);
        ReserveText(ref size, op.ActorId);
        Reserve(ref size, 16);
        ReserveText(ref size, op.NewOwnerId!);
        ReserveText(ref size, op.PriorOwnerOperationId!);
        ReserveProof(ref size, op.Authority);
        using var body = new MemoryStream(size);
        body.WriteByte(TransferType);
        WriteU16(body, WireVersion);
        WriteText(body, op.Id);
        WriteText(body, op.EntityId);
        WriteText(body, op.WriterId);
        WriteText(body, op.ActorId);
        WriteI64(body, op.Sequence);
        WriteI64(body, op.Lamport);
        WriteText(body, op.NewOwnerId!);
        WriteText(body, op.PriorOwnerOperationId!);
        WriteProof(body, op.Authority);
        return WithChecksum(body.ToArray());
    }

    private static byte[] WithChecksum(byte[] bytes)
    {
        if (bytes.Length > MaxPacketBytes - 32)
            throw new AetherProtocolException(PacketTooLarge);
        var hash = SHA256.HashData(bytes);
        var packet = new byte[bytes.Length + hash.Length];
        bytes.CopyTo(packet, 0);
        hash.CopyTo(packet, bytes.Length);
        if (packet.Length > MaxPacketBytes)
            throw new AetherProtocolException(PacketTooLarge);
        return packet;
    }

    public static byte[] EncodeContributions(IReadOnlyList<StoredField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
            throw new AetherProtocolException("Contribution packet is empty.");
        if (fields.Count > MaxFieldCount)
            throw new AetherProtocolException("Contribution packet has too many fields.");
        var entityId = fields[0].Key.EntityId;
        var writerId = fields[0].WriterId;
        foreach (var field in fields)
        {
            if (!string.Equals(field.Key.EntityId, entityId, StringComparison.Ordinal)
                || !string.Equals(field.WriterId, writerId, StringComparison.Ordinal))
                throw new AetherProtocolException("Contribution packet has one writer.");
        }

        var size = 0;
        Reserve(ref size, 1 + 2);
        ReserveText(ref size, entityId);
        ReserveText(ref size, writerId);
        Reserve(ref size, 4);
        foreach (var field in fields)
        {
            ReserveText(ref size, field.Key.FieldId);
            Reserve(ref size, 1 + 8);
            ReserveText(ref size, field.Value.Text ?? "");
            Reserve(ref size, 8);
            ReserveText(ref size, field.Version.ActorId);
            Reserve(ref size, 8);
            ReserveText(ref size, field.OperationId);
        }

        using var body = new MemoryStream(size);
        body.WriteByte(ContributionType);
        WriteU16(body, WireVersion);
        WriteText(body, entityId);
        WriteText(body, writerId);
        WriteU32(body, (uint)fields.Count);
        foreach (var field in fields)
        {
            WriteText(body, field.Key.FieldId);
            body.WriteByte(field.Value.IsText ? (byte)2 : (byte)1);
            WriteI64(body, field.Value.Fixed);
            WriteText(body, field.Value.Text ?? "");
            WriteI64(body, field.Version.Lamport);
            WriteText(body, field.Version.ActorId);
            WriteI64(body, field.Sequence);
            WriteText(body, field.OperationId);
        }

        return WithChecksum(body.ToArray());
    }

    public static bool TryDecodeContributions(
        byte[] data,
        out IReadOnlyList<StoredField> fields,
        out string? fault)
    {
        try
        {
            fields = DecodeContributions(data);
            fault = null;
            return true;
        }
        catch (AetherProtocolException ex)
        {
            fields = [];
            fault = ex.Message;
            return false;
        }
    }

    public static bool TryDecode(
        byte[] data,
        string entityId,
        string fieldId,
        out AetherOperation? operation,
        out string? fault)
    {
        try
        {
            operation = Decode(data, entityId, fieldId);
            fault = null;
            return true;
        }
        catch (AetherProtocolException ex)
        {
            operation = null;
            fault = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads a lifecycle or label packet without pinning one entity or one field.
    /// The caller decides which entities and fields belong on its link.
    /// </summary>
    public static bool TryDecodeAny(byte[] data, out AetherOperation? operation, out string? fault)
    {
        try
        {
            operation = Decode(data, null, null);
            fault = null;
            return true;
        }
        catch (AetherProtocolException ex)
        {
            operation = null;
            fault = ex.Message;
            return false;
        }
    }

    private static AetherOperation Decode(byte[] data, string? entityId, string? fieldId)
    {
        if (data.Length < 33 || data.Length > MaxPacketBytes
            || (data[0] != PacketType && data[0] != CreateType && data[0] != DeleteType && data[0] != TransferType))
            throw new AetherProtocolException(data.Length > MaxPacketBytes
                ? PacketTooLarge
                : "State packet is truncated.");
        var body = data.AsSpan(0, data.Length - 32);
        var hash = data.AsSpan(data.Length - 32);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), hash))
            throw new AetherProtocolException("State packet checksum does not match.");

        var reader = new Reader(body);
        var packetType = reader.U8();
        var version = reader.U16();
        if (version != WireVersion)
            throw new AetherProtocolException("Wire version is not supported.");
        if (packetType == CreateType)
            return DecodeCreate(ref reader, entityId);
        if (packetType == DeleteType)
            return DecodeDelete(ref reader, entityId);
        if (packetType == TransferType)
            return DecodeTransfer(ref reader, entityId);
        var rule = reader.Text();
        if (!string.Equals(rule, LabelRule, StringComparison.Ordinal))
            throw new AetherProtocolException("Rule is not lww.");
        var id = reader.Text();
        var encodedEntity = reader.Text();
        var writerId = reader.Text();
        var actorId = reader.Text();
        var sequence = reader.I64();
        var lamport = reader.I64();
        var encodedField = reader.Text();
        var label = reader.Text();
        reader.End();
        if (entityId is not null && !string.Equals(encodedEntity, entityId, StringComparison.Ordinal))
            throw new AetherProtocolException("Entity is not on this link.");
        if (fieldId is not null && !string.Equals(encodedField, fieldId, StringComparison.Ordinal))
            throw new AetherProtocolException("Field is not on this link.");

        return new AetherOperation(
            id,
            encodedEntity,
            writerId,
            actorId,
            sequence,
            lamport,
            new Dictionary<string, FieldValue> { [encodedField] = FieldValue.Label(label) });
    }

    private static AetherOperation DecodeCreate(ref Reader reader, string? entityId)
    {
        var id = reader.Text();
        var encodedEntity = reader.Text();
        var writerId = reader.Text();
        var actorId = reader.Text();
        var sequence = reader.I64();
        var lamport = reader.I64();
        var entityTypeId = reader.Text();
        var creatorId = reader.Text();
        var ownerId = reader.Text();
        var authority = ReadProof(ref reader);
        reader.End();
        if (entityId is not null && !string.Equals(encodedEntity, entityId, StringComparison.Ordinal))
            throw new AetherProtocolException("Entity is not on this link.");
        return new AetherOperation(
            id,
            encodedEntity,
            writerId,
            actorId,
            sequence,
            lamport,
            new Dictionary<string, FieldValue>(),
            AetherEffect.CreateEntity,
            entityTypeId: entityTypeId,
            creatorId: creatorId,
            ownerId: ownerId,
            authority: authority);
    }

    private static AetherOperation DecodeDelete(ref Reader reader, string? entityId)
    {
        var id = reader.Text();
        var encodedEntity = reader.Text();
        var writerId = reader.Text();
        var actorId = reader.Text();
        var sequence = reader.I64();
        var lamport = reader.I64();
        var authority = ReadProof(ref reader);
        reader.End();
        if (entityId is not null && !string.Equals(encodedEntity, entityId, StringComparison.Ordinal))
            throw new AetherProtocolException("Entity is not on this link.");
        return new AetherOperation(
            id,
            encodedEntity,
            writerId,
            actorId,
            sequence,
            lamport,
            new Dictionary<string, FieldValue>(),
            AetherEffect.DeleteEntity,
            authority: authority);
    }

    private static AetherOperation DecodeTransfer(ref Reader reader, string? entityId)
    {
        var id = reader.Text();
        var encodedEntity = reader.Text();
        var writerId = reader.Text();
        var actorId = reader.Text();
        var sequence = reader.I64();
        var lamport = reader.I64();
        var newOwnerId = reader.Text();
        var prior = reader.Text();
        var authority = ReadProof(ref reader);
        reader.End();
        if (entityId is not null && !string.Equals(encodedEntity, entityId, StringComparison.Ordinal))
            throw new AetherProtocolException("Entity is not on this link.");
        return new AetherOperation(id, encodedEntity, writerId, actorId, sequence, lamport,
            new Dictionary<string, FieldValue>(), AetherEffect.TransferOwnership,
            newOwnerId: newOwnerId, priorOwnerOperationId: prior, authority: authority);
    }

    private static void ReserveProof(ref int size, AetherAuthorityProof? proof)
    {
        Reserve(ref size, 4);
        if (proof is null)
            return;
        Reserve(ref size, proof.PublicKey.Length + proof.Signature.Length);
    }

    private static void WriteProof(Stream body, AetherAuthorityProof? proof)
    {
        WriteU16(body, checked((ushort)(proof is null ? 0 : proof.PublicKey.Length)));
        if (proof is not null) body.Write(proof.PublicKey);
        WriteU16(body, checked((ushort)(proof is null ? 0 : proof.Signature.Length)));
        if (proof is not null) body.Write(proof.Signature);
    }

    private static AetherAuthorityProof? ReadProof(ref Reader reader)
    {
        var keyLength = reader.U16();
        if (keyLength > PrincipalHandshake.MaxPublicKeyBytes)
            throw new AetherProtocolException("Authority proof is invalid.");
        var key = reader.Bytes(keyLength);
        var signatureLength = reader.U16();
        if (signatureLength > PrincipalHandshake.MaxSignatureBytes)
            throw new AetherProtocolException("Authority proof is invalid.");
        var signature = reader.Bytes(signatureLength);
        if (keyLength == 0 && signatureLength == 0)
            return null;
        if (keyLength == 0 || signatureLength == 0)
            throw new AetherProtocolException("Authority proof is invalid.");
        return new AetherAuthorityProof(key, signature);
    }

    private static IReadOnlyList<StoredField> DecodeContributions(byte[] data)
    {
        if (data.Length < 33 || data.Length > MaxPacketBytes || !IsContributionPacket(data))
            throw new AetherProtocolException(data.Length > MaxPacketBytes
                ? PacketTooLarge
                : "Contribution packet is truncated.");
        var body = data.AsSpan(0, data.Length - 32);
        var hash = data.AsSpan(data.Length - 32);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), hash))
            throw new AetherProtocolException("Contribution packet checksum does not match.");

        var reader = new Reader(body);
        reader.U8();
        var version = reader.U16();
        if (version != WireVersion)
            throw new AetherProtocolException("Wire version is not supported.");
        var entityId = reader.Text();
        var writerId = reader.Text();
        var rawCount = reader.U32();
        if (rawCount == 0 || rawCount > MaxFieldCount)
            throw new AetherProtocolException("Contribution packet has too many fields.");
        const int MinFieldBytes = 41;
        if ((long)rawCount * MinFieldBytes > reader.Remaining)
            throw new AetherProtocolException("Contribution packet is truncated.");
        var count = (int)rawCount;
        var fields = new StoredField[count];
        for (var i = 0; i < count; i++)
        {
            var fieldId = reader.Text();
            var kind = reader.U8();
            var fixedValue = reader.I64();
            var text = reader.Text();
            var lamport = reader.I64();
            var actorId = reader.Text();
            var sequence = reader.I64();
            var operationId = reader.Text();
            var value = kind switch
            {
                1 => FieldValue.FixedPoint(fixedValue),
                2 => FieldValue.Label(text),
                _ => throw new AetherProtocolException("Contribution value kind is unknown."),
            };
            fields[i] = new StoredField(
                new RecordKey(entityId, writerId, fieldId),
                value,
                new FieldVersion(lamport, actorId),
                writerId,
                operationId,
                sequence);
        }

        reader.End();
        return fields;
    }

    private static void Reserve(ref int size, int count)
    {
        if (count < 0 || size > MaxPacketBytes - 32 - count)
            throw new AetherProtocolException(PacketTooLarge);
        size += count;
    }

    private static void ReserveText(ref int size, string value)
    {
        Reserve(ref size, 4);
        Reserve(ref size, Encoding.UTF8.GetByteCount(value));
    }

    private static void WriteU16(Stream buffer, ushort value)
    {
        Span<byte> encoded = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, value);
        buffer.Write(encoded);
    }

    private static void WriteU32(Stream buffer, uint value)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        buffer.Write(encoded);
    }

    private static void WriteI64(Stream buffer, long value)
    {
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, value);
        buffer.Write(encoded);
    }

    private static void WriteText(Stream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }

    private ref struct Reader
    {
        private ReadOnlySpan<byte> _data;
        private int _offset;

        public Reader(ReadOnlySpan<byte> data) => _data = data;

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public long I64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

        public int Remaining => _data.Length - _offset;

        public string Text()
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
            if (length > MaxPacketBytes)
                throw new AetherProtocolException(PacketTooLarge);
            return Encoding.UTF8.GetString(Take((int)length));
        }

        public byte[] Bytes(int count) => Take(count).ToArray();

        public void End()
        {
            if (_offset != _data.Length)
                throw new AetherProtocolException("State packet is truncated.");
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _offset > _data.Length - count)
                throw new AetherProtocolException("State packet is truncated.");
            var slice = _data.Slice(_offset, count);
            _offset += count;
            return slice;
        }
    }
}
