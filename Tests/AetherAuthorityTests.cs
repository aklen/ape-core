using Ape.Core.Aether;
using Ape.Core.Network;

namespace Ape.Core.Tests;

public sealed class AetherAuthorityTests
{
    [Fact]
    public void Signature_binds_operation_id_entity_version_and_authority_metadata()
    {
        using var ownerDir = new TempDir();
        using var otherDir = new TempDir();
        using var owner = DurablePrincipal.Open(ownerDir.Path);
        using var other = DurablePrincipal.Open(otherDir.Path);
        var original = Seal(owner, Transfer(owner.Id, "owner-b", "entity-1", "transfer-1", 2, 8, "create-1"));

        Assert.True(AetherAuthority.Verify(original));
        Assert.False(AetherAuthority.Verify(Copy(original, entityId: "entity-2")));
        Assert.False(AetherAuthority.Verify(Copy(original, id: "transfer-2")));
        Assert.False(AetherAuthority.Verify(Copy(original, lamport: 9)));
        Assert.False(AetherAuthority.Verify(Copy(original, newOwnerId: "owner-c")));

        var wrongKey = AetherAuthority.Seal(original, other.PublicKey, other.Sign(original.AuthorityPayload()));
        Assert.False(AetherAuthority.Verify(wrongKey));
    }

    [Fact]
    public void Authenticated_ingress_requires_a_valid_authority_proof_before_queueing()
    {
        using var dir = new TempDir();
        using var owner = DurablePrincipal.Open(dir.Path);
        var reducer = Store();
        var host = new AetherHost(reducer, 4096, 8);
        var unsigned = Create(owner.Id);

        var missing = host.TryAcceptRemote(unsigned, owner.Id, requireAuthorityProof: true);
        var tampered = host.TryAcceptRemote(Copy(Seal(owner, unsigned), lamport: 2), owner.Id, requireAuthorityProof: true);
        var signed = host.TryAcceptRemote(Seal(owner, unsigned), owner.Id, requireAuthorityProof: true);

        Assert.Equal(AetherAdmitKind.Refused, missing.Kind);
        Assert.Equal("Authority proof is invalid.", missing.Reason);
        Assert.Equal(AetherAdmitKind.Refused, tampered.Kind);
        Assert.Equal("Authority proof is invalid.", tampered.Reason);
        Assert.Equal(AetherAdmitKind.Queued, signed.Kind);
        Assert.Equal(1, host.PendingCount);
        Assert.Equal(0, reducer.Clock);
    }

    [Fact]
    public void Local_signer_cannot_change_the_stamped_operation_before_state_moves()
    {
        using var dir = new TempDir();
        using var owner = DurablePrincipal.Open(dir.Path);
        var reducer = Store();

        var error = Assert.Throws<AetherProtocolException>(() => reducer.StampLocalCreate(
            "entity-1", TestStubEntity.RegistryTypeId, owner.Id, AetherActorId.Format(owner.Id, "session"),
            operation =>
            {
                var changed = new AetherOperation(operation.Id, "entity-2", operation.WriterId, operation.ActorId,
                    operation.Sequence, operation.Lamport, operation.Changes, operation.Effect,
                    entityTypeId: operation.EntityTypeId, creatorId: operation.CreatorId, ownerId: operation.OwnerId);
                return Seal(owner, changed);
            }));

        Assert.Equal("Authority signer changed or did not sign the operation.", error.Message);
        Assert.Equal(0, reducer.Clock);
        Assert.Equal(0, reducer.SaveThrough);
        Assert.Empty(reducer.ActorCursors);
        Assert.Empty(reducer.Entities);
    }

    [Fact]
    public void Wire_round_trip_preserves_the_exact_authority_proof()
    {
        using var dir = new TempDir();
        using var owner = DurablePrincipal.Open(dir.Path);
        var operation = Seal(owner, Create(owner.Id));

        var packet = AetherWire.Encode(operation);
        Assert.True(AetherWire.TryDecode(packet, "entity-1", "label", out var decoded, out var fault));

        Assert.Null(fault);
        Assert.NotNull(decoded!.Authority);
        Assert.Equal(operation.Authority, decoded.Authority);
        Assert.True(AetherAuthority.Verify(decoded));
    }

    [Fact]
    public void Snapshot_and_log_keep_create_transfer_and_delete_proofs()
    {
        using var ownerDir = new TempDir();
        using var nextDir = new TempDir();
        using var archiveDir = new TempDir();
        using var owner = DurablePrincipal.Open(ownerDir.Path);
        using var next = DurablePrincipal.Open(nextDir.Path);
        var reducer = Store();
        var create = Seal(owner, Create(owner.Id));
        reducer.Apply(create, observeClock: true);
        var archive = new AetherArchive(archiveDir.Path);
        archive.Save(reducer);
        var transfer = Seal(owner, Transfer(owner.Id, next.Id, "entity-1", "transfer-1", 2, 2, create.Id));
        archive.Append(reducer.Apply(transfer, observeClock: true));
        var delete = Seal(next, Delete(next.Id, 1, 3));
        archive.Append(reducer.Apply(delete, observeClock: true));

        var loaded = archive.Load();

        var entity = Assert.Single(loaded.Entities);
        var ownership = Assert.Single(loaded.Ownership);
        var deletion = Assert.Single(loaded.Deletions);
        Assert.Equal(create.Authority, entity.Authority);
        Assert.Equal(transfer.Authority, ownership.Authority);
        Assert.Equal(delete.Authority, deletion.Authority);
        Assert.True(AetherAuthority.Verify(new AetherOperation(entity.OperationId, entity.EntityId, entity.CreatorId,
            entity.Version.ActorId, entity.Sequence, entity.Version.Lamport, new Dictionary<string, FieldValue>(),
            AetherEffect.CreateEntity, entityTypeId: entity.EntityTypeId, creatorId: entity.CreatorId,
            ownerId: entity.OwnerId, authority: entity.Authority)));
        Assert.True(AetherAuthority.Verify(new AetherOperation(ownership.OperationId, ownership.EntityId,
            ownership.PreviousOwnerId, ownership.Version.ActorId, ownership.Sequence, ownership.Version.Lamport,
            new Dictionary<string, FieldValue>(), AetherEffect.TransferOwnership, newOwnerId: ownership.OwnerId,
            priorOwnerOperationId: ownership.PriorOperationId, authority: ownership.Authority)));
        Assert.True(AetherAuthority.Verify(new AetherOperation(deletion.OperationId, deletion.EntityId,
            deletion.WriterId, deletion.Version.ActorId, deletion.Sequence, deletion.Version.Lamport,
            new Dictionary<string, FieldValue>(), AetherEffect.DeleteEntity, authority: deletion.Authority)));
    }

    private static AetherReducer Store()
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        return reducer;
    }

    private static AetherOperation Seal(DurablePrincipal principal, AetherOperation operation) =>
        AetherAuthority.Seal(operation, principal.PublicKey, principal.Sign(operation.AuthorityPayload()));

    private static AetherOperation Create(string owner) => new("create-1", "entity-1", owner,
        AetherActorId.Format(owner, "session"), 1, 1, new Dictionary<string, FieldValue>(),
        AetherEffect.CreateEntity, entityTypeId: TestStubEntity.RegistryTypeId, creatorId: owner, ownerId: owner);

    private static AetherOperation Transfer(
        string owner, string next, string entity, string id, long sequence, long lamport, string prior) =>
        new(id, entity, owner, AetherActorId.Format(owner, "session"), sequence, lamport,
            new Dictionary<string, FieldValue>(), AetherEffect.TransferOwnership,
            newOwnerId: next, priorOwnerOperationId: prior);

    private static AetherOperation Delete(string owner, long sequence, long lamport) => new(
        "delete-1", "entity-1", owner, AetherActorId.Format(owner, "session"), sequence, lamport,
        new Dictionary<string, FieldValue>(), AetherEffect.DeleteEntity);

    private static AetherOperation Copy(
        AetherOperation source,
        string? id = null,
        string? entityId = null,
        long? lamport = null,
        string? newOwnerId = null) => new(
            id ?? source.Id, entityId ?? source.EntityId, source.WriterId, source.ActorId, source.Sequence,
            lamport ?? source.Lamport, source.Changes, source.Effect, source.PublicationId, source.EntityTypeId,
            source.CreatorId, source.OwnerId, newOwnerId ?? source.NewOwnerId, source.PriorOwnerOperationId,
            source.Authority);

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-authority-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
