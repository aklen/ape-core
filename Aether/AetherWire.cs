using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ape.Core.Aether;

/// <summary>
/// Versioned state packet for one label write. File bytes and live frames do not use this packet.
/// </summary>
public static class AetherWire
{
    public const byte PacketType = 0x30;
    public const ushort WireVersion = 1;
    public const string LabelRule = "lww";

    public static bool IsStatePacket(ReadOnlySpan<byte> data) =>
        !data.IsEmpty && data[0] == PacketType;

    public static byte[] Encode(AetherOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (op.Effect != AetherEffect.Write || op.Changes.Count != 1)
            throw new AetherProtocolException("This packet carries one label write.");
        var field = op.Changes.Single();
        if (!field.Value.IsText)
            throw new AetherProtocolException("This packet carries one label write.");

        using var body = new MemoryStream();
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
        var bytes = body.ToArray();
        var hash = SHA256.HashData(bytes);
        var packet = new byte[bytes.Length + hash.Length];
        bytes.CopyTo(packet, 0);
        hash.CopyTo(packet, bytes.Length);
        return packet;
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

    private static AetherOperation Decode(byte[] data, string entityId, string fieldId)
    {
        if (data.Length < 33 || !IsStatePacket(data))
            throw new AetherProtocolException("State packet is truncated.");
        var body = data.AsSpan(0, data.Length - 32);
        var hash = data.AsSpan(data.Length - 32);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), hash))
            throw new AetherProtocolException("State packet checksum does not match.");

        var reader = new Reader(body);
        reader.U8();
        var version = reader.U16();
        if (version != WireVersion)
            throw new AetherProtocolException("Wire version is not supported.");
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
        if (!string.Equals(encodedEntity, entityId, StringComparison.Ordinal))
            throw new AetherProtocolException("Entity is not on this link.");
        if (!string.Equals(encodedField, fieldId, StringComparison.Ordinal))
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

    private static void WriteU16(Stream buffer, ushort value)
    {
        Span<byte> encoded = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, value);
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

        public long I64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

        public string Text()
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
            if (length > int.MaxValue)
                throw new AetherProtocolException("State packet is truncated.");
            return Encoding.UTF8.GetString(Take((int)length));
        }

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
