using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Ape.Core.Network.FileTransfer;

namespace Ape.Core.Aether;

/// <summary>
/// A bounded manifest for moving a pinned checkpoint through the ordinary content-addressed chunk path.
/// The complete checkpoint digest remains the bootstrap trust input.
/// </summary>
public sealed class AetherCheckpointManifest
{
    public const int DefaultChunkSize = 64 * 1024;
    public const int MinimumChunkSize = 1024;
    public const int MaximumChunkSize = 256 * 1024;
    public const int MaximumChunks = AetherCheckpoint.DefaultMaximumBytes / MinimumChunkSize;
    private const ushort FormatVersion = 1;
    private const int DigestLength = 32;
    private static ReadOnlySpan<byte> Magic => "AECM"u8;

    private readonly byte[][] _chunkDigests;
    private readonly ReadOnlyCollection<string> _chunkSha256;

    private AetherCheckpointManifest(int artifactLength, int chunkSize, byte[] artifactDigest, byte[][] chunkDigests)
    {
        ArtifactLength = artifactLength;
        ChunkSize = chunkSize;
        ArtifactSha256 = Hex(artifactDigest);
        _chunkDigests = chunkDigests.Select(value => value.ToArray()).ToArray();
        _chunkSha256 = Array.AsReadOnly(_chunkDigests.Select(Hex).ToArray());
    }

    public int ArtifactLength { get; }
    public int ChunkSize { get; }
    public int ChunkCount => _chunkDigests.Length;
    public string ArtifactSha256 { get; }
    public IReadOnlyList<string> ChunkSha256 => _chunkSha256;

    public static AetherCheckpointManifest Create(
        AetherCheckpointArtifact artifact,
        int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireChunkSize(chunkSize);
        var bytes = artifact.ToArray();
        if (bytes.Length is < 1 or > AetherCheckpoint.DefaultMaximumBytes)
            throw new AetherProtocolException("Checkpoint is too large.");
        var count = checked((bytes.Length + chunkSize - 1) / chunkSize);
        if (count > MaximumChunks)
            throw new AetherProtocolException("Checkpoint has too many chunks.");
        var chunks = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var offset = checked(index * chunkSize);
            var length = Math.Min(chunkSize, bytes.Length - offset);
            chunks[index] = SHA256.HashData(bytes.AsSpan(offset, length));
        }
        return new AetherCheckpointManifest(bytes.Length, chunkSize,
            Convert.FromHexString(artifact.Sha256), chunks);
    }

    public byte[] Encode()
    {
        var length = checked(4 + 2 + 4 + 4 + 4 + DigestLength + ChunkCount * DigestLength + DigestLength);
        var bytes = new byte[length];
        var offset = 0;
        Magic.CopyTo(bytes);
        offset += Magic.Length;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), FormatVersion);
        offset += 2;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)ArtifactLength);
        offset += 4;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)ChunkSize);
        offset += 4;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)ChunkCount);
        offset += 4;
        Convert.FromHexString(ArtifactSha256).CopyTo(bytes, offset);
        offset += DigestLength;
        foreach (var digest in _chunkDigests)
        {
            digest.CopyTo(bytes, offset);
            offset += DigestLength;
        }
        SHA256.HashData(bytes.AsSpan(0, offset)).CopyTo(bytes, offset);
        return bytes;
    }

    public static AetherCheckpointManifest Decode(ReadOnlySpan<byte> bytes)
    {
        const int fixedLength = 4 + 2 + 4 + 4 + 4 + DigestLength + DigestLength;
        if (bytes.Length < fixedLength)
            throw new AetherProtocolException("Checkpoint manifest is truncated.");
        if (!bytes[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt16BigEndian(bytes[Magic.Length..]) != FormatVersion)
            throw new AetherProtocolException("Checkpoint manifest format is not supported.");
        var offset = Magic.Length + 2;
        var artifactLength = ReadInt(bytes, ref offset, "Checkpoint length is invalid.");
        var chunkSize = ReadInt(bytes, ref offset, "Checkpoint chunk size is invalid.");
        var count = ReadInt(bytes, ref offset, "Checkpoint chunk count is invalid.");
        RequireChunkSize(chunkSize);
        if (artifactLength is < 1 or > AetherCheckpoint.DefaultMaximumBytes)
            throw new AetherProtocolException("Checkpoint length is invalid.");
        var expectedCount = checked((artifactLength + chunkSize - 1) / chunkSize);
        if (count != expectedCount || count > MaximumChunks)
            throw new AetherProtocolException("Checkpoint chunk count is invalid.");
        var expectedLength = checked(fixedLength + count * DigestLength);
        if (bytes.Length != expectedLength)
            throw new AetherProtocolException("Checkpoint manifest length is invalid.");
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes[..^DigestLength]), bytes[^DigestLength..]))
            throw new AetherProtocolException("Checkpoint manifest checksum failed.");
        var artifactDigest = bytes.Slice(offset, DigestLength).ToArray();
        offset += DigestLength;
        var chunks = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            chunks[index] = bytes.Slice(offset, DigestLength).ToArray();
            offset += DigestLength;
        }
        return new AetherCheckpointManifest(artifactLength, chunkSize, artifactDigest, chunks);
    }

    public int ChunkLength(int index)
    {
        RequireIndex(index);
        return index == ChunkCount - 1
            ? ArtifactLength - checked(index * ChunkSize)
            : ChunkSize;
    }

    internal bool MatchesChunk(int index, ReadOnlySpan<byte> bytes)
    {
        RequireIndex(index);
        return bytes.Length == ChunkLength(index)
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), _chunkDigests[index]);
    }

    private void RequireIndex(int index)
    {
        if ((uint)index >= (uint)ChunkCount)
            throw new AetherProtocolException("Checkpoint chunk index is invalid.");
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, ref int offset, string fault)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
        offset += 4;
        if (value > int.MaxValue)
            throw new AetherProtocolException(fault);
        return (int)value;
    }

    private static void RequireChunkSize(int chunkSize)
    {
        if (chunkSize is < MinimumChunkSize or > MaximumChunkSize)
            throw new AetherProtocolException("Checkpoint chunk size is invalid.");
    }

    private static string Hex(byte[] digest) => Convert.ToHexString(digest).ToLowerInvariant();
}

/// <summary>
/// Reassembles verified checkpoint chunks from a content-addressed store. Recreating this
/// object with the same pinned manifest resumes from chunks already present in that store.
/// </summary>
public sealed class AetherCheckpointReceiver
{
    private readonly AetherCheckpointManifest _manifest;
    private readonly IChunkStore _chunks;
    private readonly string _expectedSha256;

    public AetherCheckpointReceiver(
        AetherCheckpointManifest manifest,
        string expectedSha256,
        IChunkStore chunks)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(chunks);
        var expected = ParseDigest(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(manifest.ArtifactSha256)))
            throw new AetherProtocolException("Checkpoint manifest does not match the pinned digest.");
        _manifest = manifest;
        _chunks = chunks;
        _expectedSha256 = expectedSha256.ToLowerInvariant();
    }

    public async Task<IReadOnlyList<int>> MissingChunksAsync(CancellationToken cancellationToken = default)
    {
        var missing = new List<int>();
        for (var index = 0; index < _manifest.ChunkCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = _manifest.ChunkSha256[index];
            if (!_chunks.HasChunk(hash))
            {
                missing.Add(index);
                continue;
            }
            var bytes = await _chunks.GetChunkAsync(hash, cancellationToken).ConfigureAwait(false);
            if (bytes is null || !_manifest.MatchesChunk(index, bytes))
                missing.Add(index);
        }
        return missing;
    }

    public async Task AcceptChunkAsync(int index, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!_manifest.MatchesChunk(index, bytes))
            throw new AetherProtocolException("Checkpoint chunk checksum failed.");
        var expected = _manifest.ChunkSha256[index];
        if (_chunks.HasChunk(expected))
        {
            var stored = await _chunks.GetChunkAsync(expected, cancellationToken).ConfigureAwait(false);
            if (stored is not null && _manifest.MatchesChunk(index, stored))
                return;
            await _chunks.DeleteChunkAsync(expected, cancellationToken).ConfigureAwait(false);
        }
        var actual = await _chunks.StoreChunkAsync(bytes.ToArray(), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new AetherProtocolException("Chunk store returned the wrong digest.");
    }

    public async Task<AetherReducer> CompleteAsync(CancellationToken cancellationToken = default)
    {
        var artifact = new byte[_manifest.ArtifactLength];
        var offset = 0;
        for (var index = 0; index < _manifest.ChunkCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await _chunks.GetChunkAsync(_manifest.ChunkSha256[index], cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null)
                throw new AetherProtocolException("Checkpoint is incomplete.");
            if (!_manifest.MatchesChunk(index, bytes))
                throw new AetherProtocolException("Checkpoint chunk checksum failed.");
            bytes.CopyTo(artifact, offset);
            offset += bytes.Length;
        }
        return AetherCheckpoint.LoadPinned(artifact, _expectedSha256, _manifest.ArtifactLength);
    }

    public static byte[] ReadChunk(
        AetherCheckpointArtifact artifact,
        AetherCheckpointManifest manifest,
        int index)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(artifact.Sha256, manifest.ArtifactSha256, StringComparison.OrdinalIgnoreCase)
            || artifact.Length != manifest.ArtifactLength)
            throw new AetherProtocolException("Checkpoint does not match its manifest.");
        var length = manifest.ChunkLength(index);
        var chunk = artifact.CopyRange(checked(index * manifest.ChunkSize), length);
        if (!manifest.MatchesChunk(index, chunk))
            throw new AetherProtocolException("Checkpoint does not match its manifest.");
        return chunk;
    }

    private static byte[] ParseDigest(string text)
    {
        if (text is null || text.Length != 64)
            throw new AetherProtocolException("Checkpoint digest is invalid.");
        try { return Convert.FromHexString(text); }
        catch (FormatException) { throw new AetherProtocolException("Checkpoint digest is invalid."); }
    }
}
