using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ape.Core.Aether;

internal enum AetherCheckpointPacketKind : byte
{
    ManifestRequest = 0x40,
    Manifest = 0x41,
    ChunkRequest = 0x42,
    Chunk = 0x43,
}

internal readonly record struct AetherCheckpointPacket(
    AetherCheckpointPacketKind Kind,
    string ArtifactSha256,
    int ChunkIndex = -1,
    byte[]? Payload = null);

internal static class AetherCheckpointWire
{
    public const ushort Version = 1;
    public const int MaximumPacketBytes = AetherCheckpointManifest.MaximumChunkSize + 1024;
    private const int DigestLength = 32;
    private const int ChecksumLength = 32;

    public static bool IsPacket(ReadOnlySpan<byte> bytes) =>
        !bytes.IsEmpty && bytes[0] is >= (byte)AetherCheckpointPacketKind.ManifestRequest
            and <= (byte)AetherCheckpointPacketKind.Chunk;

    public static byte[] Encode(AetherCheckpointPacket packet)
    {
        var digest = ParseDigest(packet.ArtifactSha256);
        var payload = packet.Payload ?? [];
        var variable = packet.Kind switch
        {
            AetherCheckpointPacketKind.ManifestRequest => 0,
            AetherCheckpointPacketKind.Manifest => checked(4 + payload.Length),
            AetherCheckpointPacketKind.ChunkRequest => 4,
            AetherCheckpointPacketKind.Chunk => checked(8 + payload.Length),
            _ => throw new AetherProtocolException("Checkpoint packet kind is unknown."),
        };
        if (packet.Kind is AetherCheckpointPacketKind.ManifestRequest or AetherCheckpointPacketKind.ChunkRequest
            && payload.Length != 0)
            throw new AetherProtocolException("Checkpoint request has a payload.");
        if (packet.Kind is AetherCheckpointPacketKind.ChunkRequest or AetherCheckpointPacketKind.Chunk
            && packet.ChunkIndex < 0)
            throw new AetherProtocolException("Checkpoint chunk index is invalid.");
        var bytes = new byte[checked(1 + 2 + DigestLength + variable + ChecksumLength)];
        bytes[0] = (byte)packet.Kind;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), Version);
        digest.CopyTo(bytes, 3);
        var offset = 3 + DigestLength;
        if (packet.Kind == AetherCheckpointPacketKind.Manifest)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)payload.Length);
            offset += 4;
            payload.CopyTo(bytes, offset);
            offset += payload.Length;
        }
        else if (packet.Kind == AetherCheckpointPacketKind.ChunkRequest)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)packet.ChunkIndex);
            offset += 4;
        }
        else if (packet.Kind == AetherCheckpointPacketKind.Chunk)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)packet.ChunkIndex);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), (uint)payload.Length);
            offset += 8;
            payload.CopyTo(bytes, offset);
            offset += payload.Length;
        }
        SHA256.HashData(bytes.AsSpan(0, offset)).CopyTo(bytes, offset);
        if (bytes.Length > MaximumPacketBytes)
            throw new AetherProtocolException("Checkpoint packet is too large.");
        return bytes;
    }

    public static AetherCheckpointPacket Decode(ReadOnlySpan<byte> bytes)
    {
        const int fixedLength = 1 + 2 + DigestLength + ChecksumLength;
        if (bytes.Length < fixedLength)
            throw new AetherProtocolException("Checkpoint packet is truncated.");
        if (bytes.Length > MaximumPacketBytes)
            throw new AetherProtocolException("Checkpoint packet is too large.");
        if (!Enum.IsDefined(typeof(AetherCheckpointPacketKind), bytes[0])
            || BinaryPrimitives.ReadUInt16BigEndian(bytes[1..]) != Version)
            throw new AetherProtocolException("Checkpoint packet format is not supported.");
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes[..^ChecksumLength]), bytes[^ChecksumLength..]))
            throw new AetherProtocolException("Checkpoint packet checksum failed.");
        var kind = (AetherCheckpointPacketKind)bytes[0];
        var digest = Convert.ToHexString(bytes.Slice(3, DigestLength)).ToLowerInvariant();
        var offset = 3 + DigestLength;
        var bodyEnd = bytes.Length - ChecksumLength;
        if (kind == AetherCheckpointPacketKind.ManifestRequest)
        {
            RequireEnd(offset, bodyEnd);
            return new AetherCheckpointPacket(kind, digest);
        }
        if (kind == AetherCheckpointPacketKind.ChunkRequest)
        {
            RequireRemaining(offset, bodyEnd, 4);
            var index = ReadIndex(bytes, ref offset);
            RequireEnd(offset, bodyEnd);
            return new AetherCheckpointPacket(kind, digest, index);
        }
        RequireRemaining(offset, bodyEnd, 4);
        var first = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
        offset += 4;
        if (kind == AetherCheckpointPacketKind.Manifest)
        {
            var length = ToLength(first, bodyEnd - offset);
            var payload = bytes.Slice(offset, length).ToArray();
            offset += length;
            RequireEnd(offset, bodyEnd);
            return new AetherCheckpointPacket(kind, digest, Payload: payload);
        }
        var chunkIndex = ToIndex(first);
        RequireRemaining(offset, bodyEnd, 4);
        var chunkLength = ToLength(BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]), bodyEnd - (offset + 4));
        offset += 4;
        var chunk = bytes.Slice(offset, chunkLength).ToArray();
        offset += chunkLength;
        RequireEnd(offset, bodyEnd);
        return new AetherCheckpointPacket(kind, digest, chunkIndex, chunk);
    }

    private static int ReadIndex(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
        offset += 4;
        return ToIndex(value);
    }
    private static int ToIndex(uint value)
    {
        if (value > int.MaxValue)
            throw new AetherProtocolException("Checkpoint chunk index is invalid.");
        return (int)value;
    }
    private static int ToLength(uint value, int remaining)
    {
        if (value > int.MaxValue || value != remaining)
            throw new AetherProtocolException("Checkpoint packet length is invalid.");
        return (int)value;
    }
    private static void RequireRemaining(int offset, int end, int count)
    {
        if (offset > end - count)
            throw new AetherProtocolException("Checkpoint packet is truncated.");
    }
    private static void RequireEnd(int offset, int end)
    {
        if (offset != end)
            throw new AetherProtocolException("Checkpoint packet length is invalid.");
    }
    private static byte[] ParseDigest(string digest)
    {
        if (digest is null || digest.Length != DigestLength * 2)
            throw new AetherProtocolException("Checkpoint digest is invalid.");
        try { return Convert.FromHexString(digest); }
        catch (FormatException) { throw new AetherProtocolException("Checkpoint digest is invalid."); }
    }
}
