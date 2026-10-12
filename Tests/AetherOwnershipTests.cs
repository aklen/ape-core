using Ape.Core.Aether;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
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
