using Ape.Core.Aether;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Tests;

public sealed class AetherCreateTests
{
    [Fact]
    public void Local_create_records_immutable_creator_owner_and_type()
    {
        var reducer = Store();
        var actor = AetherActorId.Format("principal-a", "session-1");

        var create = reducer.StampLocalCreate(
            "entity-1",
            TestStubEntity.RegistryTypeId,
            "principal-a",
            actor);
        reducer.Apply(create, observeClock: false);

        var entity = Assert.Single(reducer.Entities);
        Assert.Equal("entity-1", entity.EntityId);
        Assert.Equal(TestStubEntity.RegistryTypeId, entity.EntityTypeId);
        Assert.Equal("principal-a", entity.CreatorId);
        Assert.Equal("principal-a", entity.OwnerId);
        Assert.Equal(create.Id, entity.OperationId);
        Assert.Equal(create.Sequence, entity.Sequence);
        Assert.Equal(new FieldVersion(create.Lamport, actor), entity.Version);

        var clock = reducer.Clock;
        reducer.Apply(create, observeClock: true);
        Assert.Equal(clock, reducer.Clock);
        Assert.Single(reducer.Entities);
    }

    [Fact]
    public void Remote_create_must_name_the_authenticated_principal_as_creator_and_owner()
    {
        var reducer = Store();
        var host = new AetherHost(reducer, 4096, 8);
        var forged = Create(
            "create-1",
            "principal-a",
            sequence: 1,
            lamport: 1,
            creator: "principal-b",
            owner: "principal-b");

        var admit = host.TryAcceptRemote(forged, "principal-a");

        Assert.Equal(AetherAdmitKind.Refused, admit.Kind);
        Assert.Equal("Create is not owned by this connection.", admit.Reason);
        Assert.Equal(0, host.PendingCount);
        Assert.Equal(0, reducer.Clock);
        Assert.Empty(reducer.Entities);
    }

    [Fact]
    public void Conflicting_create_is_rejected_before_any_state_changes()
    {
        var reducer = Store();
        reducer.Apply(Create("create-a", "principal-a", 1, 1), observeClock: true);
        var clock = reducer.Clock;
        var saveThrough = reducer.SaveThrough;
        var before = Assert.Single(reducer.Entities);
        var conflict = Create(
            "create-b",
            "principal-b",
            sequence: 1,
            lamport: 10,
            entityTypeId: "tests.other.v1");

        var error = Assert.Throws<AetherProtocolException>(() => reducer.Apply(conflict, observeClock: true));

        Assert.Contains("conflicting creation metadata", error.Message, StringComparison.Ordinal);
        Assert.Equal(clock, reducer.Clock);
        Assert.Equal(saveThrough, reducer.SaveThrough);
        Assert.Equal(before, Assert.Single(reducer.Entities));
    }

    [Fact]
    public void Wire_round_trip_preserves_the_create_claim()
    {
        var create = Create("create-a", "principal-a", 7, 19);

        var packet = AetherWire.Encode(create);
        var decoded = AetherWire.TryDecode(packet, "entity-1", "label", out var result, out var fault);

        Assert.True(decoded);
        Assert.Null(fault);
        Assert.Equal(AetherWire.CreateType, packet[0]);
        Assert.Equal(create.Id, result!.Id);
        Assert.Equal(create.Digest, result.Digest);
        Assert.Equal(AetherEffect.CreateEntity, result.Effect);
        Assert.Equal(TestStubEntity.RegistryTypeId, result.EntityTypeId);
        Assert.Equal("principal-a", result.CreatorId);
        Assert.Equal("principal-a", result.OwnerId);
    }

    [Fact]
    public void Oversized_local_create_is_rejected_before_stamping()
    {
        var reducer = Store();
        var host = new AetherHost(reducer, 200_000, 8);
        var admit = host.TryAcceptLocalCreate(
            "entity-1",
            new string('x', AetherWire.MaxPacketBytes),
            "principal-a",
            AetherActorId.Format("principal-a", "session-1"));
        Assert.Equal(AetherAdmitKind.Queued, admit.Kind);

        var outcome = Assert.Single(host.Drain());

        Assert.Equal(AetherOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(AetherWire.PacketTooLarge, outcome.Reason);
        Assert.Equal(0, reducer.Clock);
        Assert.Equal(0, reducer.SaveThrough);
        Assert.Empty(reducer.Entities);
        Assert.Empty(reducer.ActorCursors);
    }

    [Fact]
    public void Snapshot_and_recovery_log_restore_creation_and_actor_frontier()
    {
        using var directory = new TempDir();
        var archive = new AetherArchive(directory.Path);
        var reducer = Store();
        archive.Save(reducer);
        var create = Create("create-a", "principal-a", 7, 19);
        var applied = reducer.Apply(create, observeClock: true);
        archive.Append(applied);

        var fromLog = archive.Load();
        Assert.Equal(reducer.Clock, fromLog.Clock);
        Assert.Equal(reducer.SaveThrough, fromLog.SaveThrough);
        Assert.Equal(7, Assert.Single(fromLog.ActorCursors).Sequence);
        Assert.Equal(Assert.Single(reducer.Entities), Assert.Single(fromLog.Entities));

        archive.Save(reducer);
        var fromSnapshot = archive.Load();
        Assert.Equal(Assert.Single(reducer.Entities), Assert.Single(fromSnapshot.Entities));
        var next = fromSnapshot.StampLocal(
            "entity-1",
            "principal-a",
            AetherActorId.Format("principal-a", "session-1"),
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("next") });
        Assert.Equal(8, next.Sequence);
    }

    [Fact]
    public void Update_before_create_projects_only_after_create_arrives()
    {
        var reducer = Store();
        var actor = AetherActorId.Format("principal-a", "session-1");
        reducer.Apply(new AetherOperation(
            "write-a",
            "entity-1",
            "principal-a",
            actor,
            1,
            1,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("early") }), observeClock: true);
        reducer.Apply(new AetherOperation(
            "publish-a",
            "entity-1",
            "principal-a",
            actor,
            2,
            2,
            new Dictionary<string, FieldValue>(),
            AetherEffect.Publish,
            "catalog"), observeClock: true);

        var plan = AetherFrame.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = Scratch(reducer);
        plan.Tick(ref scratch, ref runtime);
        Assert.DoesNotContain(scratch.SceneCommits!, request => request is EnsureAetherEntityCommitRequest);

        var scene = EmptyScene(out var logger);
        Apply(scene, logger, scratch.SceneCommits!);
        Assert.Null(scene.GetEntity("entity-1"));

        reducer.Apply(Create("create-a", "principal-a", 3, 3), observeClock: true);
        plan.Tick(ref scratch, ref runtime);
        Assert.IsType<EnsureAetherEntityCommitRequest>(scratch.SceneCommits![0]);
        Apply(scene, logger, scratch.SceneCommits);

        var entity = Assert.IsType<TestStubEntity>(scene.GetEntity("entity-1"));
        Assert.Equal("early", entity.Label);
        Assert.Equal("principal-a", ((Replica)entity).OwnerId);
    }

    private static AetherScratch Scratch(AetherReducer reducer) => new()
    {
        Reducer = reducer,
        Inbox = new List<AetherOperation>(),
        EntityId = "entity-1",
        LabelFieldId = "label",
        SumFieldId = "offset",
        PublicationId = "catalog",
        SceneEntityKey = "entity-1",
        LabelPropertyName = nameof(TestStubEntity.Label),
    };

    private static AetherReducer Store()
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        reducer.DefineField("offset", "sumContributions");
        return reducer;
    }

    private static AetherOperation Create(
        string id,
        string principal,
        long sequence,
        long lamport,
        string? creator = null,
        string? owner = null,
        string entityTypeId = TestStubEntity.RegistryTypeId) =>
        new(
            id,
            "entity-1",
            principal,
            AetherActorId.Format(principal, "session-1"),
            sequence,
            lamport,
            new Dictionary<string, FieldValue>(),
            AetherEffect.CreateEntity,
            entityTypeId: entityTypeId,
            creatorId: creator ?? principal,
            ownerId: owner ?? principal);

    private static ISceneManager EmptyScene(out ILogger logger)
    {
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        logger = services.GetRequiredService<ILogger>();
        return services.GetRequiredService<ISceneManager>();
    }

    private static void Apply(ISceneManager scene, ILogger logger, IReadOnlyList<ISceneCommitRequest> requests)
    {
        foreach (var request in requests)
            SceneCommitApplicator.Apply(scene, request, logger);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-create-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
