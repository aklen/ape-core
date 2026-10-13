using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ape.Core.Network;

namespace Ape.Core.Aether;

/// <summary>
/// A compact, owner-signed state head for one entity. Bootstrap accepts it only when
/// the complete artifact also matches a digest obtained through a trusted channel.
/// </summary>
public static class AetherCheckpoint
{
    public const int DefaultMaximumBytes = 4 * 1024 * 1024;
    private const ushort FormatVersion = 1;
    private const int ChecksumLength = 32;
    private static ReadOnlySpan<byte> Magic => "AECP"u8;

    public static AetherCheckpointArtifact Create(
        AetherReducer source,
        string entityId,
        INetworkManager identity,
        int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        ArgumentNullException.ThrowIfNull(identity);
        RequireMaximum(maximumBytes);

        var owner = source.GetOwner(entityId)
            ?? throw new AetherProtocolException("Checkpoint entity is unknown.");
        if (!string.Equals(owner, identity.PrincipalId, StringComparison.Ordinal))
            throw new AetherProtocolException("Checkpoint signer is not the current owner.");
        if (identity is not IPrincipalSigner signer || !signer.HasPrincipalSigner)
            throw new AetherProtocolException("Checkpoint signer is unavailable.");

        RequireAuthorityHead(source, entityId);
        var selected = SelectEntity(source, entityId);
        ValidateLoaded(selected, entityId, owner);
        var state = AetherArchive.EncodeState(selected);
        var signingPayload = SigningPayload(entityId, owner, state);
        if (!signer.TrySignPrincipal(signingPayload, out var publicKey, out var signature))
            throw new AetherProtocolException("Checkpoint signer is unavailable.");
        if (!Fingerprint(publicKey).Equals(owner, StringComparison.Ordinal)
            || !DurablePrincipal.IsCanonicalP256(publicKey)
            || !DurablePrincipal.Verify(publicKey, signingPayload, signature))
            throw new AetherProtocolException("Checkpoint signature is invalid.");

        using var body = new MemoryStream();
        body.Write(Magic);
        WriteU16(body, FormatVersion);
        WriteU16(body, AetherArchive.StateFormatVersion);
        WriteText(body, entityId);
        WriteText(body, owner);
        WriteBytes(body, state);
        WriteBytes(body, publicKey);
        WriteBytes(body, signature);
        var prefix = body.ToArray();
        var bytes = new byte[checked(prefix.Length + ChecksumLength)];
        prefix.CopyTo(bytes, 0);
        SHA256.HashData(prefix).CopyTo(bytes, prefix.Length);
        if (bytes.Length > maximumBytes)
            throw new AetherProtocolException("Checkpoint is too large.");
        return new AetherCheckpointArtifact(bytes);
    }

    public static AetherReducer LoadPinned(
        ReadOnlySpan<byte> bytes,
        string expectedSha256,
        int maximumBytes = DefaultMaximumBytes)
    {
        RequireMaximum(maximumBytes);
        if (bytes.Length > maximumBytes)
            throw new AetherProtocolException("Checkpoint is too large.");
        var expected = ParseDigest(expectedSha256);
        var actual = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new AetherProtocolException("Checkpoint digest does not match.");
        if (bytes.Length < Magic.Length + 4 + ChecksumLength)
            throw new AetherProtocolException("Checkpoint is truncated.");

        var prefix = bytes[..^ChecksumLength];
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(prefix), bytes[^ChecksumLength..]))
            throw new AetherProtocolException("Checkpoint checksum failed.");

        var reader = new Reader(prefix);
        if (!reader.Bytes(Magic.Length).SequenceEqual(Magic))
            throw new AetherProtocolException("Checkpoint format is not supported.");
        if (reader.U16() != FormatVersion || reader.U16() != AetherArchive.StateFormatVersion)
            throw new AetherProtocolException("Checkpoint format is not supported.");
        var entityId = reader.Text();
        var issuer = reader.Text();
        var state = reader.LengthPrefixedBytes(maximumBytes);
        var publicKey = reader.LengthPrefixedBytes(PrincipalHandshake.MaxPublicKeyBytes);
        var signature = reader.LengthPrefixedBytes(PrincipalHandshake.MaxSignatureBytes);
        reader.End();

        var signingPayload = SigningPayload(entityId, issuer, state);
        if (!Fingerprint(publicKey).Equals(issuer, StringComparison.Ordinal)
            || !DurablePrincipal.IsCanonicalP256(publicKey)
            || !DurablePrincipal.Verify(publicKey, signingPayload, signature))
            throw new AetherProtocolException("Checkpoint signature is invalid.");

        var reducer = AetherArchive.DecodeState(state);
        ValidateLoaded(reducer, entityId, issuer);
        return reducer;
    }

    private static AetherReducer SelectEntity(AetherReducer source, string entityId)
    {
        var image = source.Capture();
        var fields = image.Fields.Where(row => row.Key.EntityId == entityId).ToArray();
        var visibility = (image.Visibility ?? []).Where(row => row.EntityId == entityId).ToArray();
        var membership = (image.Membership ?? []).Where(row => row.EntityId == entityId).ToArray();
        var entities = (image.Entities ?? []).Where(row => row.EntityId == entityId).ToArray();
        var deletions = (image.Deletions ?? []).Where(row => row.EntityId == entityId).ToArray();
        var ownership = (image.Ownership ?? []).Where(row => row.EntityId == entityId).ToArray();
        var deleted = (image.Deleted ?? []).Where(id => id == entityId).ToArray();
        var actors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields) actors.Add(field.Version.ActorId);
        foreach (var mark in visibility) actors.Add(mark.Version.ActorId);
        foreach (var mark in membership) actors.Add(mark.Version.ActorId);
        foreach (var entity in entities) actors.Add(entity.Version.ActorId);
        foreach (var deletion in deletions) actors.Add(deletion.Version.ActorId);
        foreach (var row in ownership) actors.Add(row.Version.ActorId);

        var selected = new AetherReducer(source.DedupCapacity);
        foreach (var definition in source.Schema)
            selected.DefineField(definition.Id, definition.Resolver, definition.Min, definition.Max);
        selected.MergeImage(new AetherImage(image.Clock, fields, [], deleted, visibility, membership,
            entities, deletions, ownership));
        foreach (var cursor in source.ActorCursors.Where(cursor => actors.Contains(cursor.ActorId)))
            selected.RestoreActorSequence(cursor.ActorId, cursor.Sequence, cursor.Lamport, cursor.OperationId);
        return selected;
    }

    private static void ValidateLoaded(AetherReducer reducer, string entityId, string issuer)
    {
        var image = reducer.Capture();
        if (string.IsNullOrEmpty(entityId)
            || image.Fields.Any(row => row.Key.EntityId != entityId)
            || (image.Visibility ?? []).Any(row => row.EntityId != entityId)
            || (image.Membership ?? []).Any(row => row.EntityId != entityId)
            || (image.Entities ?? []).Count != 1
            || (image.Entities ?? [])[0].EntityId != entityId
            || (image.Deletions ?? []).Any(row => row.EntityId != entityId)
            || (image.Ownership ?? []).Any(row => row.EntityId != entityId))
            throw new AetherProtocolException("Checkpoint state does not match its entity.");
        if (!string.Equals(reducer.GetOwner(entityId), issuer, StringComparison.Ordinal))
            throw new AetherProtocolException("Checkpoint issuer is not the current owner.");
        RequireAuthorityHead(reducer, entityId);
    }

    private static void RequireAuthorityHead(AetherReducer reducer, string entityId)
    {
        var entity = reducer.GetEntity(entityId)
            ?? throw new AetherProtocolException("Checkpoint entity is unknown.");
        var create = new AetherOperation(entity.OperationId, entity.EntityId, entity.CreatorId,
            entity.Version.ActorId, entity.Sequence, entity.Version.Lamport, new Dictionary<string, FieldValue>(),
            AetherEffect.CreateEntity, entityTypeId: entity.EntityTypeId, creatorId: entity.CreatorId,
            ownerId: entity.OwnerId, authority: entity.Authority);
        if (!AetherAuthority.Verify(create))
            throw new AetherProtocolException("Checkpoint authority proof is missing or invalid.");

        if (reducer.GetOwnership(entityId) is { } ownership)
        {
            var transfer = new AetherOperation(ownership.OperationId, ownership.EntityId,
                ownership.PreviousOwnerId, ownership.Version.ActorId, ownership.Sequence,
                ownership.Version.Lamport, new Dictionary<string, FieldValue>(), AetherEffect.TransferOwnership,
                newOwnerId: ownership.OwnerId, priorOwnerOperationId: ownership.PriorOperationId,
                authority: ownership.Authority);
            if (!AetherAuthority.Verify(transfer))
                throw new AetherProtocolException("Checkpoint authority proof is missing or invalid.");
        }

        if (reducer.GetDeletion(entityId) is { } deletion)
        {
            var delete = new AetherOperation(deletion.OperationId, deletion.EntityId, deletion.WriterId,
                deletion.Version.ActorId, deletion.Sequence, deletion.Version.Lamport,
                new Dictionary<string, FieldValue>(), AetherEffect.DeleteEntity, authority: deletion.Authority);
            if (!AetherAuthority.Verify(delete))
                throw new AetherProtocolException("Checkpoint authority proof is missing or invalid.");
        }
    }

    private static byte[] SigningPayload(string entityId, string issuer, byte[] state)
    {
        using var payload = new MemoryStream();
        WriteText(payload, "ape-aether-checkpoint-v1");
        WriteU16(payload, AetherArchive.StateFormatVersion);
        WriteText(payload, entityId);
        WriteText(payload, issuer);
        WriteU32(payload, checked((uint)state.Length));
        payload.Write(SHA256.HashData(state));
        return payload.ToArray();
    }

    private static byte[] ParseDigest(string text)
    {
        if (text is null || text.Length != ChecksumLength * 2)
            throw new AetherProtocolException("Checkpoint digest is invalid.");
        try { return Convert.FromHexString(text); }
        catch (FormatException) { throw new AetherProtocolException("Checkpoint digest is invalid."); }
    }

    private static string Fingerprint(ReadOnlySpan<byte> key) =>
        Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();

    private static void RequireMaximum(int maximumBytes)
    {
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    private static void WriteText(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));
    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteU32(stream, checked((uint)value.Length));
        stream.Write(value);
    }
    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _offset;
        public Reader(ReadOnlySpan<byte> bytes) => _bytes = bytes;
        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public string Text() => Encoding.UTF8.GetString(LengthPrefixedSpan(DefaultMaximumBytes));
        public byte[] LengthPrefixedBytes(int maximum) => LengthPrefixedSpan(maximum).ToArray();
        public ReadOnlySpan<byte> Bytes(int count) => Take(count);
        public void End()
        {
            if (_offset != _bytes.Length)
                throw new AetherProtocolException("Checkpoint has trailing bytes.");
        }
        private ReadOnlySpan<byte> LengthPrefixedSpan(int maximum)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
            if (length > maximum || length > int.MaxValue)
                throw new AetherProtocolException("Checkpoint field is too large.");
            return Take((int)length);
        }
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _offset > _bytes.Length - count)
                throw new AetherProtocolException("Checkpoint is truncated.");
            var result = _bytes.Slice(_offset, count);
            _offset += count;
            return result;
        }
    }
}

public sealed class AetherCheckpointArtifact
{
    private readonly byte[] _bytes;
    internal AetherCheckpointArtifact(byte[] bytes)
    {
        _bytes = bytes;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
    public string Sha256 { get; }
    public int Length => _bytes.Length;
    public byte[] ToArray() => _bytes.ToArray();
    internal byte[] CopyRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > _bytes.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _bytes.AsSpan(offset, length).ToArray();
    }
}
