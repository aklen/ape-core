using Ape.Core.Aether;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Network.Services;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Tests;

public sealed class AetherOwnershipTests
{
    [Fact]
    public void Explicit_transfer_changes_owner_but_not_creator()
    {
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);

        var transfer = Transfer("owner-a", "owner-b", 2, 2, create.Id);
        Apply(reducer, transfer);

        Assert.Equal("owner-a", Assert.Single(reducer.Entities).CreatorId);
        Assert.Equal("owner-a", Assert.Single(reducer.Entities).OwnerId);
        Assert.Equal("owner-b", reducer.GetOwner("entity-1"));
        Assert.Equal(transfer.Id, reducer.GetOwnerOperationId("entity-1"));
        Assert.Equal(1, Assert.Single(reducer.Ownership).Generation);
    }

    [Fact]
    public void Transfer_requires_current_owner_and_current_prior_operation()
    {
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);
        var clock = reducer.Clock;
        var through = reducer.SaveThrough;

        var foreign = Assert.Throws<AetherProtocolException>(() =>
            Apply(reducer, Transfer("intruder", "owner-b", 1, 2, create.Id)));
        var stale = Assert.Throws<AetherProtocolException>(() =>
            Apply(reducer, Transfer("owner-a", "owner-b", 2, 2, "wrong")));

        Assert.Equal("Ownership transfer is not issued by the entity owner.", foreign.Message);
        Assert.Equal("Ownership transfer prior operation does not match.", stale.Message);
        Assert.Equal(clock, reducer.Clock);
        Assert.Equal(through, reducer.SaveThrough);
        Assert.Equal("owner-a", reducer.GetOwner("entity-1"));
        Assert.Empty(reducer.Ownership);
    }

    [Fact]
    public void New_owner_can_delete_and_old_owner_cannot()
    {
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);
        Apply(reducer, Transfer("owner-a", "owner-b", 2, 2, create.Id));

        Assert.Throws<AetherProtocolException>(() => Apply(reducer, Delete("owner-a", 3, 3)));
        Apply(reducer, Delete("owner-b", 1, 3));

        Assert.True(reducer.IsDeleted("entity-1"));
        Assert.Equal("owner-b", Assert.Single(reducer.Deletions).WriterId);
    }

    [Fact]
    public void Ownership_is_a_sequential_chain_and_a_deleted_entity_cannot_transfer()
    {
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);
        var first = Transfer("owner-a", "owner-b", 2, 2, create.Id);
        Apply(reducer, first);
        var second = Transfer("owner-b", "owner-c", 1, 3, first.Id);
        Apply(reducer, second);

        Assert.Equal("owner-c", reducer.GetOwner("entity-1"));
        Assert.Equal(2, Assert.Single(reducer.Ownership).Generation);
        Apply(reducer, Delete("owner-c", 1, 4));
        var clock = reducer.Clock;

        var error = Assert.Throws<AetherProtocolException>(() =>
            Apply(reducer, Transfer("owner-c", "owner-d", 2, 5, second.Id)));

        Assert.Equal("Deleted entity ownership cannot change.", error.Message);
        Assert.Equal(clock, reducer.Clock);
        Assert.Equal("owner-c", reducer.GetOwner("entity-1"));
    }

    [Fact]
    public void Wire_round_trip_preserves_transfer_chain()
    {
        var op = Transfer("owner-a", "owner-b", 2, 8, "create-op");

        var packet = AetherWire.Encode(op);
        Assert.True(AetherWire.TryDecode(packet, "entity-1", "label", out var decoded, out var fault));

        Assert.Null(fault);
        Assert.Equal(AetherWire.TransferType, packet[0]);
        Assert.Equal(op.Digest, decoded!.Digest);
        Assert.Equal("owner-b", decoded.NewOwnerId);
        Assert.Equal("create-op", decoded.PriorOwnerOperationId);
    }

    [Fact]
    public void Exact_transfer_replay_is_idempotent_after_the_digest_window_evicts_it()
    {
        var reducer = new AetherReducer(1);
        reducer.DefineField("label", "lww");
        var create = Create();
        Apply(reducer, create);
        var transfer = Transfer("owner-a", "owner-b", 2, 2, create.Id);
        Apply(reducer, transfer);
        Apply(reducer, new AetherOperation("other", "other", "owner-c", Actor("owner-c"), 1, 3,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("x") }));
        Assert.False(reducer.Remembers(transfer.Id));
        var host = new AetherHost(reducer, 4096, 8);

        var admit = host.TryAcceptRemote(transfer, "owner-a");
        var outcome = Assert.Single(host.Drain());

        Assert.Equal(AetherAdmitKind.Queued, admit.Kind);
        Assert.Equal(AetherOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("owner-b", reducer.GetOwner("entity-1"));
        Assert.Equal(1, Assert.Single(reducer.Ownership).Generation);
    }

    [Fact]
    public void Snapshot_and_log_restore_current_owner_and_chain()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);
        archive.Save(reducer);
        var transfer = reducer.Apply(Transfer("owner-a", "owner-b", 2, 2, create.Id), observeClock: true);
        archive.Append(transfer);

        var fromLog = archive.Load();
        Assert.Equal("owner-b", fromLog.GetOwner("entity-1"));
        Assert.Equal(transfer.Operation!.Id, Assert.Single(fromLog.Ownership).OperationId);

        archive.Save(reducer);
        var fromSnapshot = archive.Load();
        Assert.Equal(Assert.Single(reducer.Ownership), Assert.Single(fromSnapshot.Ownership));
        var next = fromSnapshot.StampLocalTransfer("entity-1", "owner-c", "owner-b", Actor("owner-b"));
        Assert.Equal(transfer.Operation.Id, next.PriorOwnerOperationId);
    }

    [Fact]
    public void Host_refuses_transfer_before_queue_when_principal_is_not_owner()
    {
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);
        var host = new AetherHost(reducer, 4096, 8);

        var admit = host.TryAcceptRemote(Transfer("intruder", "owner-b", 1, 2, create.Id), "intruder");

        Assert.Equal(AetherAdmitKind.Refused, admit.Kind);
        Assert.Equal(0, host.PendingCount);
        Assert.Equal("owner-a", reducer.GetOwner("entity-1"));
    }

    [Fact]
    public void Frame_projects_the_current_owner_into_existing_scene_entity()
    {
        var reducer = Store();
        var create = Create();
        Apply(reducer, create);
        Apply(reducer, Transfer("owner-a", "owner-b", 2, 2, create.Id));
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = services.GetRequiredService<ISceneManager>();
        var logger = services.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1", "owner-a"), logger);
        var plan = AetherFrame.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = new AetherScratch
        {
            Reducer = reducer, Inbox = [], EntityId = "entity-1", LabelFieldId = "label",
            SceneEntityKey = "entity-1",
        };

        plan.Tick(ref scratch, ref runtime);
        foreach (var request in scratch.SceneCommits!) SceneCommitApplicator.Apply(scene, request, logger);

        Assert.Equal("owner-b", Assert.IsAssignableFrom<Replica>(scene.GetEntity("entity-1")).OwnerId);
    }

    [Fact]
    public void Transfer_keeps_the_previous_owners_hide_and_refuses_a_new_one()
    {
        using var previousDir = new TempDir();
        using var nextDir = new TempDir();
        using var saveDir = new TempDir();
        using var previous = new NetworkManager("none", previousDir.Path);
        using var next = new NetworkManager("none", nextDir.Path);
        var previousActor = AetherActorId.Format(previous.PrincipalId, "s1");
        var nextActor = AetherActorId.Format(next.PrincipalId, "s1");
        var source = Store();
        var create = Signed(previous, new AetherOperation("create", "entity-1", previous.PrincipalId,
            previousActor, 1, 1, new Dictionary<string, FieldValue>(), AetherEffect.CreateEntity,
            entityTypeId: TestStubEntity.RegistryTypeId, creatorId: previous.PrincipalId,
            ownerId: previous.PrincipalId));
        Apply(source, create);
        var hide = Signed(previous, new AetherOperation("hide-1", "entity-1", previous.PrincipalId,
            previousActor, 2, 2, new Dictionary<string, FieldValue>(), AetherEffect.HideShared));
        Apply(source, hide);
        var publish = Signed(previous, new AetherOperation("publish-1", "entity-1", previous.PrincipalId,
            previousActor, 3, 3, new Dictionary<string, FieldValue>(), AetherEffect.Publish, "catalog"));
        Apply(source, publish);
        var transfer = source.StampLocalTransfer("entity-1", next.PrincipalId, previous.PrincipalId,
            previousActor, operation => Signed(previous, operation));
        Apply(source, transfer);

        Assert.Equal("hide-1", transfer.FrozenVisibilityOperationId);
        Assert.True(AetherReducer.FrozenMembershipContains(transfer.FrozenMembershipOperationIds, "publish-1"));
        Assert.True(AetherWire.TryDecodeAny(AetherWire.Encode(transfer), out var decoded, out _));
        Assert.Equal(transfer.FrozenVisibilityOperationId, decoded!.FrozenVisibilityOperationId);
        Assert.Equal(transfer.FrozenMembershipOperationIds, decoded.FrozenMembershipOperationIds);

        var receiver = Store();
        Apply(receiver, create);
        Apply(receiver, transfer);
        new AetherArchive(saveDir.Path).Save(receiver);
        var loaded = new AetherArchive(saveDir.Path).Load();
        Assert.Equal("hide-1", Assert.Single(loaded.Ownership).FrozenVisibilityOperationId);
        Apply(loaded, hide);
        Apply(loaded, publish);
        Assert.False(loaded.IsSharedVisible("entity-1"));
        Assert.True(loaded.IsMember("entity-1", "catalog"));

        var laterHide = Signed(previous, new AetherOperation("hide-2", "entity-1", previous.PrincipalId,
            previousActor, 6, 8, new Dictionary<string, FieldValue>(), AetherEffect.HideShared));
        var reused = Signed(previous, new AetherOperation("hide-1", "entity-1", previous.PrincipalId,
            previousActor, 7, 9, new Dictionary<string, FieldValue>(), AetherEffect.Publish, "catalog"));
        var hideError = Assert.Throws<AetherProtocolException>(() => Apply(loaded, laterHide));
        var unseen = Store();
        Apply(unseen, create);
        Apply(unseen, transfer);
        var publishError = Assert.Throws<AetherProtocolException>(() => Apply(unseen, reused));
        Assert.Equal("Hide is not issued by the entity owner.", hideError.Message);
        Assert.Equal("Publication is not issued by the entity owner.", publishError.Message);
        Assert.False(loaded.IsSharedVisible("entity-1"));
        Assert.True(loaded.IsMember("entity-1", "catalog"));

        var restore = Signed(next, new AetherOperation("restore-1", "entity-1", next.PrincipalId,
            nextActor, 1, 10, new Dictionary<string, FieldValue>(), AetherEffect.RestoreShared));
        Apply(loaded, restore);
        Apply(loaded, hide);
        Assert.True(loaded.IsSharedVisible("entity-1"));

        var queued = Store();
        Apply(queued, create);
        Apply(queued, transfer);
        var host = new AetherHost(queued, 64 * 1024, 16);
        var admitted = host.TryAcceptRemote(hide, previous.PrincipalId, requireAuthorityProof: true);
        var refused = host.TryAcceptRemote(laterHide, previous.PrincipalId, requireAuthorityProof: true);
        Assert.Equal(AetherAdmitKind.Queued, admitted.Kind);
        Assert.Equal(AetherAdmitKind.Refused, refused.Kind);
        Assert.Equal("Hide is not issued by the entity owner.", refused.Reason);
        host.Drain();
        Assert.False(queued.IsSharedVisible("entity-1"));
    }

    private static AetherOperation Signed(NetworkManager identity, AetherOperation operation)
    {
        var signer = (IPrincipalSigner)identity;
        Assert.True(signer.TrySignPrincipal(operation.AuthorityPayload(), out var key, out var signature));
        return AetherAuthority.Seal(operation, key, signature);
    }

    private static AetherReducer Store()
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        return reducer;
    }

    private static AetherApplied Apply(AetherReducer reducer, AetherOperation op) => reducer.Apply(op, observeClock: true);

    private static AetherOperation Create() => new("create-op", "entity-1", "owner-a", Actor("owner-a"), 1, 1,
        new Dictionary<string, FieldValue>(), AetherEffect.CreateEntity,
        entityTypeId: TestStubEntity.RegistryTypeId, creatorId: "owner-a", ownerId: "owner-a");

    private static AetherOperation Transfer(string writer, string next, long sequence, long lamport, string prior) =>
        new($"{Actor(writer)}/{sequence}", "entity-1", writer, Actor(writer), sequence, lamport,
            new Dictionary<string, FieldValue>(), AetherEffect.TransferOwnership,
            newOwnerId: next, priorOwnerOperationId: prior);

    private static AetherOperation Delete(string writer, long sequence, long lamport) =>
        new($"{Actor(writer)}/{sequence}", "entity-1", writer, Actor(writer), sequence, lamport,
            new Dictionary<string, FieldValue>(), AetherEffect.DeleteEntity);

    private static string Actor(string owner) => AetherActorId.Format(owner, "session");

    private sealed class TempDir : IDisposable
    {
        public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-owner-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
