using System.Buffers.Binary;
using System.Text;

namespace Ape.Core.Network;

/// <summary>
/// Binds a remote durable principal to one connection slot.
/// The bytes are a claim, not a proof. The process instance id is not this value.
/// </summary>
public static class PeerHello
{
    public const byte Marker = 0xA5;
    private const byte Version = 1;
    public const int MaxPrincipalBytes = 128;
    public const int MaxBytes = 4 + MaxPrincipalBytes;

    public static byte[] Encode(string principalId)
    {
        if (string.IsNullOrEmpty(principalId))
            throw new ArgumentException("Principal is missing.", nameof(principalId));
        var body = Encoding.UTF8.GetBytes(principalId);
        if (body.Length > MaxPrincipalBytes)
            throw new ArgumentException("Principal is too long.", nameof(principalId));

        var packet = new byte[4 + body.Length];
        packet[0] = Marker;
        packet[1] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)body.Length);
        body.CopyTo(packet, 4);
        return packet;
    }

    public static bool HasMarker(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == Marker && data[1] == Version;

    public static bool IsHello(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data.Length <= MaxBytes && data[0] == Marker && data[1] == Version;

    public static bool TryDecode(ReadOnlySpan<byte> data, out string principalId)
    {
        principalId = "";
        if (!IsHello(data))
            return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
        if (length == 0 || length > MaxPrincipalBytes || data.Length != 4 + length)
            return false;
        principalId = Encoding.UTF8.GetString(data.Slice(4, length));
        return principalId.Length > 0;
    }
}
