using System.Security.Cryptography;
using Ape.Core.Aether;
using Ape.Core.Network;
using Ape.Core.Network.Services;

namespace Ape.Core.Tests;

public sealed class AetherCheckpointTests
{
    [Fact]
    public void Pinned_checkpoint_restores_one_entity_and_its_current_authority_head()
    {
        using var firstDir = new TempDir();
        using var secondDir = new TempDir();
        using var first = new NetworkManager("none", firstDir.Path);
        using var second = new NetworkManager("none", secondDir.Path);
        var source = Store();
        var firstActor = AetherActorId.Format(first.PrincipalId, "s1");
        var secondActor = AetherActorId.Format(second.PrincipalId, "s1");
        var create = Signed(first, new AetherOperation("create", "entity-1", first.PrincipalId,
            firstActor, 1, 1, Empty(), AetherEffect.CreateEntity, entityTypeId: TestStubEntity.RegistryTypeId,
            creatorId: first.PrincipalId, ownerId: first.PrincipalId));
        source.Apply(create, observeClock: true);
        source.Apply(new AetherOperation("write", "entity-1", first.PrincipalId, firstActor, 2, 2,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("alpha") }), observeClock: true);
        source.Apply(new AetherOperation("publish", "entity-1", first.PrincipalId, firstActor, 3, 3,
            Empty(), AetherEffect.Publish, "catalog"), observeClock: true);
        source.Apply(Signed(first, new AetherOperation("transfer", "entity-1", first.PrincipalId,
            firstActor, 4, 4, Empty(), AetherEffect.TransferOwnership, newOwnerId: second.PrincipalId,
            priorOwnerOperationId: create.Id)), observeClock: true);
        source.Apply(new AetherOperation("other", "entity-2", first.PrincipalId, firstActor, 5, 5,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("excluded") }), observeClock: true);

        var artifact = AetherCheckpoint.Create(source, "entity-1", second);
        var restored = AetherCheckpoint.LoadPinned(artifact.ToArray(), artifact.Sha256);

        Assert.Equal("alpha", restored.ResolveLww("entity-1", "label")?.Text);
        Assert.Null(restored.ResolveLww("entity-2", "label")?.Text);
        Assert.True(restored.IsMember("entity-1", "catalog"));
        Assert.Equal(first.PrincipalId, restored.GetEntity("entity-1")!.CreatorId);
        Assert.Equal(second.PrincipalId, restored.GetOwner("entity-1"));
        Assert.Equal("transfer", restored.GetOwnerOperationId("entity-1"));
        Assert.Contains(restored.ActorCursors, row => row.ActorId == firstActor && row.Sequence == 5);
        Assert.DoesNotContain(restored.ActorCursors, row => row.ActorId == secondActor);
        Assert.Equal(source.Clock, restored.Clock);
    }

    [Fact]
    public void Bootstrap_rejects_a_wrong_pin_before_parsing_state()
    {
        using var dir = new TempDir();
        using var owner = new NetworkManager("none", dir.Path);
        var source = Created(owner);
        var artifact = AetherCheckpoint.Create(source, "entity-1", owner);

        var error = Assert.Throws<AetherProtocolException>(() =>
            AetherCheckpoint.LoadPinned(artifact.ToArray(), new string('0', 64)));

        Assert.Equal("Checkpoint digest does not match.", error.Message);
    }

    [Fact]
    public void Bootstrap_rejects_a_tampered_but_rehashed_artifact()
    {
        using var dir = new TempDir();
        using var owner = new NetworkManager("none", dir.Path);
        var artifact = AetherCheckpoint.Create(Created(owner), "entity-1", owner);
        var bytes = artifact.ToArray();
        bytes[^33] ^= 1;
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));
        var repinned = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var error = Assert.Throws<AetherProtocolException>(() => AetherCheckpoint.LoadPinned(bytes, repinned));

        Assert.Equal("Checkpoint signature is invalid.", error.Message);
    }

    [Fact]
    public void Only_the_current_owner_can_issue_a_checkpoint()
    {
        using var firstDir = new TempDir();
        using var secondDir = new TempDir();
        using var first = new NetworkManager("none", firstDir.Path);
        using var second = new NetworkManager("none", secondDir.Path);
        var source = Created(first);
        var create = source.GetEntity("entity-1")!;
        source.Apply(Signed(first, new AetherOperation("transfer", "entity-1", first.PrincipalId,
            AetherActorId.Format(first.PrincipalId, "s1"), 2, 2, Empty(), AetherEffect.TransferOwnership,
            newOwnerId: second.PrincipalId, priorOwnerOperationId: create.OperationId)), observeClock: true);

        var error = Assert.Throws<AetherProtocolException>(() =>
            AetherCheckpoint.Create(source, "entity-1", first));

        Assert.Equal("Checkpoint signer is not the current owner.", error.Message);
        Assert.NotNull(AetherCheckpoint.Create(source, "entity-1", second));
    }

    [Fact]
    public void Unsigned_lifecycle_state_cannot_become_a_bootstrap_checkpoint()
    {
        using var dir = new TempDir();
        using var owner = new NetworkManager("none", dir.Path);
        var source = Store();
        source.Apply(new AetherOperation("create", "entity-1", owner.PrincipalId,
            AetherActorId.Format(owner.PrincipalId, "s1"), 1, 1, Empty(), AetherEffect.CreateEntity,
            entityTypeId: TestStubEntity.RegistryTypeId, creatorId: owner.PrincipalId,
            ownerId: owner.PrincipalId), observeClock: true);

        var error = Assert.Throws<AetherProtocolException>(() =>
            AetherCheckpoint.Create(source, "entity-1", owner));

        Assert.Equal("Checkpoint authority proof is missing or invalid.", error.Message);
    }

    [Fact]
    public void Deleted_entity_remains_deleted_after_bootstrap()
    {
        using var dir = new TempDir();
        using var owner = new NetworkManager("none", dir.Path);
        var source = Created(owner);
        source.Apply(Signed(owner, new AetherOperation("delete", "entity-1", owner.PrincipalId,
            AetherActorId.Format(owner.PrincipalId, "s1"), 2, 2, Empty(), AetherEffect.DeleteEntity)),
            observeClock: true);

        var artifact = AetherCheckpoint.Create(source, "entity-1", owner);
        var restored = AetherCheckpoint.LoadPinned(artifact.ToArray(), artifact.Sha256);

        Assert.True(restored.IsDeleted("entity-1"));
        Assert.NotNull(restored.GetDeletion("entity-1")!.Authority);
    }

    [Fact]
    public void Checkpoint_size_limit_applies_before_acceptance()
    {
        using var dir = new TempDir();
        using var owner = new NetworkManager("none", dir.Path);
        var source = Created(owner);

        var createError = Assert.Throws<AetherProtocolException>(() =>
            AetherCheckpoint.Create(source, "entity-1", owner, 32));
        var artifact = AetherCheckpoint.Create(source, "entity-1", owner);
        var loadError = Assert.Throws<AetherProtocolException>(() =>
            AetherCheckpoint.LoadPinned(artifact.ToArray(), artifact.Sha256, artifact.Length - 1));

        Assert.Equal("Checkpoint is too large.", createError.Message);
        Assert.Equal("Checkpoint is too large.", loadError.Message);
    }

    [Fact]
    public void State_decoder_rejects_an_impossible_count_before_allocating()
    {
        var body = new byte[24];
        body[11] = 1;
        body[20] = 0xff;
        body[21] = 0xff;
        body[22] = 0xff;
        body[23] = 0xff;

        var error = Assert.Throws<AetherProtocolException>(() => AetherArchive.DecodeState(body));

        Assert.Equal("Snapshot schema count is invalid.", error.Message);
    }

    private static AetherReducer Created(NetworkManager owner)
    {
        var source = Store();
        source.Apply(Signed(owner, new AetherOperation("create", "entity-1", owner.PrincipalId,
            AetherActorId.Format(owner.PrincipalId, "s1"), 1, 1, Empty(), AetherEffect.CreateEntity,
            entityTypeId: TestStubEntity.RegistryTypeId, creatorId: owner.PrincipalId,
            ownerId: owner.PrincipalId)), observeClock: true);
        return source;
    }

    private static AetherReducer Store()
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        reducer.DefineField("offset", "sumContributions", -100, 100);
        return reducer;
    }

    private static AetherOperation Signed(NetworkManager identity, AetherOperation operation)
    {
        var signer = (IPrincipalSigner)identity;
        Assert.True(signer.TrySignPrincipal(operation.AuthorityPayload(), out var key, out var signature));
        return AetherAuthority.Seal(operation, key, signature);
    }

    private static IReadOnlyDictionary<string, FieldValue> Empty() =>
        new Dictionary<string, FieldValue>();

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
