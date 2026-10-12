using System.Globalization;
using System.Text;

namespace Ape.Core.Aether;

/// <summary>
/// One actor belonging to one transport peer and one session.
/// The text form is length-prefixed, so one peer id cannot pass as a prefix of another.
/// </summary>
public readonly record struct AetherActorId(string PeerId, string SessionId)
{
    public static string Format(string peerId, string sessionId)
    {
        if (string.IsNullOrEmpty(peerId) || string.IsNullOrEmpty(sessionId))
            throw new AetherProtocolException("Actor sequence is missing.");
        var peerBytes = Encoding.UTF8.GetByteCount(peerId);
        var sessionBytes = Encoding.UTF8.GetByteCount(sessionId);
        return string.Concat(
            peerBytes.ToString(CultureInfo.InvariantCulture),
            ":",
            peerId,
            sessionBytes.ToString(CultureInfo.InvariantCulture),
            ":",
            sessionId);
    }

    public static bool TryParse(string text, out AetherActorId actor)
    {
        actor = default;
        if (string.IsNullOrEmpty(text))
            return false;

        var bytes = Encoding.UTF8.GetBytes(text);
        var offset = 0;
        if (!TrySegment(bytes, ref offset, out var peer) || peer.Length == 0)
            return false;
        if (!TrySegment(bytes, ref offset, out var session) || session.Length == 0)
            return false;
        if (offset != bytes.Length)
            return false;

        actor = new AetherActorId(peer, session);
        return true;
    }

    private static bool TrySegment(byte[] bytes, ref int offset, out string value)
    {
        value = "";
        var start = offset;
        while (offset < bytes.Length && bytes[offset] is >= (byte)'0' and <= (byte)'9')
            offset++;
        if (offset == start || offset >= bytes.Length || bytes[offset] != (byte)':')
            return false;
        if (!int.TryParse(
                Encoding.ASCII.GetString(bytes, start, offset - start),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var length)
            || length < 0
            || length > bytes.Length - (offset + 1))
            return false;

        offset++;
        value = Encoding.UTF8.GetString(bytes, offset, length);
        offset += length;
        return true;
    }
}
