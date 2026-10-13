using System.Buffers.Binary;
using System.Security.Cryptography;
using Ape.Core.Aether;
using Ape.Core.Network;
using Ape.Core.Network.FileTransfer;
using Ape.Core.Network.Services;
using Ape.Core.Replication;

namespace Ape.Core.Tests;

public sealed class AetherCheckpointTransferTests
{
    [Fact]
    public async Task Chunks_arrive_out_of_order_and_resume_from_the_content_store()
    {
        using var profile = new TempDir();
        using var storage = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var artifact = Checkpoint(owner, new string('a', 3500));
        var manifest = AetherCheckpointManifest.Create(artifact, AetherCheckpointManifest.MinimumChunkSize);
        var decoded = AetherCheckpointManifest.Decode(manifest.Encode());
        var chunks = new FileChunkStore(storage.Path);
        var first = new AetherCheckpointReceiver(decoded, artifact.Sha256, chunks);

        Assert.True(decoded.ChunkCount > 2);
        Assert.Equal(Enumerable.Range(0, decoded.ChunkCount), await first.MissingChunksAsync());
        var last = decoded.ChunkCount - 1;
        var lastBytes = AetherCheckpointReceiver.ReadChunk(artifact, decoded, last);
        await first.AcceptChunkAsync(last, lastBytes);
        await first.AcceptChunkAsync(last, lastBytes);

        var resumed = new AetherCheckpointReceiver(decoded, artifact.Sha256, chunks);
        Assert.DoesNotContain(last, await resumed.MissingChunksAsync());
        foreach (var index in await resumed.MissingChunksAsync())
            await resumed.AcceptChunkAsync(index, AetherCheckpointReceiver.ReadChunk(artifact, decoded, index));

        var restored = await resumed.CompleteAsync();

        Assert.Equal(new string('a', 3500), restored.ResolveLww("entity-1", "label")?.Text);
        Assert.Empty(await resumed.MissingChunksAsync());
    }

    [Fact]
    public async Task Corrupt_chunk_is_rejected_before_it_enters_the_store()
    {
        using var profile = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var artifact = Checkpoint(owner, "alpha");
        var manifest = AetherCheckpointManifest.Create(artifact);
        var store = new MemoryChunkStore();
        var receiver = new AetherCheckpointReceiver(manifest, artifact.Sha256, store);
        var chunk = AetherCheckpointReceiver.ReadChunk(artifact, manifest, 0);
        chunk[0] ^= 1;

        var error = await Assert.ThrowsAsync<AetherProtocolException>(() => receiver.AcceptChunkAsync(0, chunk));

        Assert.Equal("Checkpoint chunk checksum failed.", error.Message);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Manifest_must_name_the_out_of_band_pin()
    {
        using var profile = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var artifact = Checkpoint(owner, "alpha");
        var manifest = AetherCheckpointManifest.Create(artifact);

        var error = Assert.Throws<AetherProtocolException>(() =>
            new AetherCheckpointReceiver(manifest, new string('0', 64), new MemoryChunkStore()));

        Assert.Equal("Checkpoint manifest does not match the pinned digest.", error.Message);
    }

    [Fact]
    public void Manifest_rejects_an_impossible_chunk_count_before_allocating()
    {
        using var profile = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var bytes = AetherCheckpointManifest.Create(Checkpoint(owner, "alpha")).Encode();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), uint.MaxValue);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));

        var error = Assert.Throws<AetherProtocolException>(() => AetherCheckpointManifest.Decode(bytes));

        Assert.Equal("Checkpoint chunk count is invalid.", error.Message);
    }

    [Fact]
    public async Task Completion_rechecks_content_store_bytes_and_the_whole_artifact()
    {
        using var profile = new TempDir();
        using var owner = new NetworkManager("none", profile.Path);
        var artifact = Checkpoint(owner, "alpha");
        var manifest = AetherCheckpointManifest.Create(artifact);
        var store = new MemoryChunkStore();
        var receiver = new AetherCheckpointReceiver(manifest, artifact.Sha256, store);
        await receiver.AcceptChunkAsync(0, AetherCheckpointReceiver.ReadChunk(artifact, manifest, 0));
        store.Corrupt(manifest.ChunkSha256[0]);

        var error = await Assert.ThrowsAsync<AetherProtocolException>(() => receiver.CompleteAsync());

        Assert.Equal("Checkpoint chunk checksum failed.", error.Message);
        Assert.Equal([0], await receiver.MissingChunksAsync());
    }

    private static AetherCheckpointArtifact Checkpoint(NetworkManager owner, string label)
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        var actor = AetherActorId.Format(owner.PrincipalId, "session");
        reducer.Apply(Signed(owner, new AetherOperation("create", "entity-1", owner.PrincipalId,
            actor, 1, 1, Empty(), AetherEffect.CreateEntity, entityTypeId: TestStubEntity.RegistryTypeId,
            creatorId: owner.PrincipalId, ownerId: owner.PrincipalId)), observeClock: true);
        reducer.Apply(new AetherOperation("write", "entity-1", owner.PrincipalId, actor, 2, 2,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label(label) }), observeClock: true);
        return AetherCheckpoint.Create(reducer, "entity-1", owner);
    }

    private static AetherOperation Signed(NetworkManager identity, AetherOperation operation)
    {
        var signer = (IPrincipalSigner)identity;
        Assert.True(signer.TrySignPrincipal(operation.AuthorityPayload(), out var key, out var signature));
        return AetherAuthority.Seal(operation, key, signature);
    }

    private static IReadOnlyDictionary<string, FieldValue> Empty() => new Dictionary<string, FieldValue>();

    private sealed class MemoryChunkStore : IChunkStore
    {
        private readonly Dictionary<string, byte[]> _chunks = new(StringComparer.OrdinalIgnoreCase);
        public int Count => _chunks.Count;
        public Task<string> StoreChunkAsync(byte[] data, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            _chunks[hash] = data.ToArray();
            return Task.FromResult(hash);
        }
        public Task<byte[]?> GetChunkAsync(string hash, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_chunks.TryGetValue(hash, out var value) ? value.ToArray() : null);
        }
        public bool HasChunk(string hash) => _chunks.ContainsKey(hash);
        public Task DeleteChunkAsync(string hash, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _chunks.Remove(hash);
            return Task.CompletedTask;
        }
        public long GetStorageSize() => _chunks.Values.Sum(value => (long)value.Length);
        public void Corrupt(string hash) => _chunks[hash][0] ^= 1;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
