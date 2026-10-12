using System.Buffers.Binary;

namespace Ape.Core.Network;

/// <summary>
/// Mutual nonce exchange and a signature over one versioned transcript.
/// A checked signature shows that the profile key is present. It does not grant authority.
/// </summary>
public static class PrincipalHandshake
{
    public const byte Marker = 0xA6;
    public const byte Version = 1;
    public const int NonceLength = 32;
    public const int MaxPublicKeyBytes = 256;
    public const int MaxSignatureBytes = 128;
    public const int MaxBytes = 3 + 32 + 2 + MaxPublicKeyBytes + NonceLength;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private const byte HelloType = 1;
    private const byte ProofType = 2;
    private static ReadOnlySpan<byte> Domain => "ape-handshake-v1"u8;

    internal readonly record struct Message(
        byte Kind,
        byte[] Fingerprint,
        byte[] PublicKey,
        byte[] Nonce,
        byte[] Signature);

    internal static byte[] EncodeHello(DurablePrincipal signer, ReadOnlySpan<byte> nonce) =>
        EncodeHello(signer.FingerprintBytes, signer.PublicKey, nonce);

    internal static byte[] EncodeHello(ReadOnlySpan<byte> fingerprint, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> nonce)
    {
        if (fingerprint.Length != DurablePrincipal.ByteLength)
            throw new ArgumentException("Fingerprint is the wrong length.", nameof(fingerprint));
        if (publicKey.Length == 0 || publicKey.Length > MaxPublicKeyBytes)
            throw new ArgumentException("Public key is the wrong length.", nameof(publicKey));
        if (nonce.Length != NonceLength)
            throw new ArgumentException("Nonce is the wrong length.", nameof(nonce));

        var packet = new byte[3 + fingerprint.Length + 2 + publicKey.Length + nonce.Length];
        packet[0] = Marker;
        packet[1] = Version;
        packet[2] = HelloType;
        fingerprint.CopyTo(packet.AsSpan(3));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3 + fingerprint.Length), (ushort)publicKey.Length);
        publicKey.CopyTo(packet.AsSpan(5 + fingerprint.Length));
        nonce.CopyTo(packet.AsSpan(5 + fingerprint.Length + publicKey.Length));
        return packet;
    }

    internal static byte[] EncodeProof(ReadOnlySpan<byte> signature)
    {
        if (signature.Length == 0 || signature.Length > MaxSignatureBytes)
            throw new ArgumentException("Signature is the wrong length.", nameof(signature));
        var packet = new byte[5 + signature.Length];
        packet[0] = Marker;
        packet[1] = Version;
        packet[2] = ProofType;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), (ushort)signature.Length);
        signature.CopyTo(packet.AsSpan(5));
        return packet;
    }

    internal static byte[] Transcript(
        ReadOnlySpan<byte> signerFingerprint,
        ReadOnlySpan<byte> peerFingerprint,
        ReadOnlySpan<byte> signerNonce,
        ReadOnlySpan<byte> peerNonce,
        ReadOnlySpan<byte> signerKey,
        ReadOnlySpan<byte> peerKey)
    {
        var buffer = new byte[
            2
            + Domain.Length
            + 2 + signerFingerprint.Length
            + 2 + peerFingerprint.Length
            + 2 + signerNonce.Length
            + 2 + peerNonce.Length
            + 2 + signerKey.Length
            + 2 + peerKey.Length];
        var span = buffer.AsSpan();
        Write(ref span, Version);
        Domain.CopyTo(span);
        span = span[Domain.Length..];
        WriteBytes(ref span, signerFingerprint);
        WriteBytes(ref span, peerFingerprint);
        WriteBytes(ref span, signerNonce);
        WriteBytes(ref span, peerNonce);
        WriteBytes(ref span, signerKey);
        WriteBytes(ref span, peerKey);
        if (!span.IsEmpty)
            throw new InvalidOperationException("Handshake transcript size is wrong.");
        return buffer;
    }

    internal static bool HasMarker(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == Marker && data[1] == Version;

    internal static bool TryRead(ReadOnlySpan<byte> data, out Message message)
    {
        message = default;
        if (!HasMarker(data) || data.Length > MaxBytes || data.Length < 3)
            return false;
        if (data[2] == HelloType)
            return TryReadHello(data, out message);
        if (data[2] == ProofType)
            return TryReadProof(data, out message);
        return false;
    }

    private static bool TryReadHello(ReadOnlySpan<byte> data, out Message message)
    {
        message = default;
        if (data.Length < 3 + DurablePrincipal.ByteLength + 2 + NonceLength)
            return false;
        var fingerprint = data.Slice(3, DurablePrincipal.ByteLength).ToArray();
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3 + DurablePrincipal.ByteLength));
        if (keyLength == 0 || keyLength > MaxPublicKeyBytes)
            return false;
        var keyAt = 5 + DurablePrincipal.ByteLength;
        if (data.Length != keyAt + keyLength + NonceLength)
            return false;
        message = new Message(
            HelloType,
            fingerprint,
            data.Slice(keyAt, keyLength).ToArray(),
            data.Slice(keyAt + keyLength, NonceLength).ToArray(),
            []);
        return true;
    }

    private static bool TryReadProof(ReadOnlySpan<byte> data, out Message message)
    {
        message = default;
        if (data.Length < 5)
            return false;
        var signatureLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3));
        if (signatureLength == 0 || signatureLength > MaxSignatureBytes || data.Length != 5 + signatureLength)
            return false;
        message = new Message(ProofType, [], [], [], data.Slice(5, signatureLength).ToArray());
        return true;
    }

    private static void Write(ref Span<byte> span, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        span = span[2..];
    }

    private static void WriteBytes(ref Span<byte> span, ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
            throw new ArgumentException("Handshake field is too long.");
        Write(ref span, (ushort)value.Length);
        value.CopyTo(span);
        span = span[value.Length..];
    }
}
