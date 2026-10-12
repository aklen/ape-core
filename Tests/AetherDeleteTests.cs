using Ape.Core.Aether;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Tests;

public sealed class AetherDeleteTests
{
    [Fact]
    public void Owner_delete_is_terminal_and_keeps_its_original_claim()
    {
        var reducer = Store();
        Apply(reducer, Create("owner", 1, 1));
        var delete = Delete("owner", 2, 2);

        Apply(reducer, delete);
        Apply(reducer, Write("owner", 3, 3, "after"));

        Assert.True(reducer.IsDeleted("entity-1"));
        Assert.Equal("after", reducer.ResolveLww("entity-1", "label")?.Text);
        var stored = Assert.Single(reducer.Deletions);
        Assert.Equal("owner", stored.WriterId);
        Assert.Equal(delete.Id, stored.OperationId);
        Assert.Equal(delete.Sequence, stored.Sequence);
        Assert.Equal(new FieldVersion(delete.Lamport, delete.ActorId), stored.Version);
    }

    [Fact]
    public void Non_owner_delete_is_rejected_before_mutation()
    {
        var reducer = Store();
        Apply(reducer, Create("owner", 1, 1));
        var clock = reducer.Clock;
        var through = reducer.SaveThrough;
        var cursors = reducer.ActorCursors.ToArray();

        var error = Assert.Throws<AetherProtocolException>(() =>
            Apply(reducer, Delete("intruder", 1, 10)));

        Assert.Equal("Delete is not issued by the entity owner.", error.Message);
        Assert.False(reducer.IsDeleted("entity-1"));
        Assert.Equal(clock, reducer.Clock);
        Assert.Equal(through, reducer.SaveThrough);
        Assert.Equal(cursors, reducer.ActorCursors);
        Assert.Empty(reducer.Deletions);
    }

    [Fact]
    public void Remote_delete_requires_the_authenticated_owner_before_the_queue()
    {
        var reducer = Store();
        Apply(reducer, Create("owner", 1, 1));
        var host = new AetherHost(reducer, 4096, 8);

        var admit = host.TryAcceptRemote(Delete("intruder", 1, 10), "intruder");

        Assert.Equal(AetherAdmitKind.Refused, admit.Kind);
        Assert.Equal("Delete is not issued by the entity owner.", admit.Reason);
        Assert.Equal(0, host.PendingCount);
        Assert.False(reducer.IsDeleted("entity-1"));
    }

    [Fact]
    public void Wire_round_trip_preserves_the_delete_envelope()
    {
        var delete = Delete("owner", 8, 21);

        var packet = AetherWire.Encode(delete);
        var decoded = AetherWire.TryDecode(packet, "entity-1", "label", out var result, out var fault);

        Assert.True(decoded);
        Assert.Null(fault);
        Assert.Equal(AetherWire.DeleteType, packet[0]);
        Assert.Equal(delete.Id, result!.Id);
        Assert.Equal(delete.Digest, result.Digest);
        Assert.Equal(AetherEffect.DeleteEntity, result.Effect);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Frame_removes_the_scene_entity_but_keeps_raw_state()
    {
        var reducer = Store();
        Apply(reducer, Create("owner", 1, 1));
        Apply(reducer, Write("owner", 2, 2, "kept"));
        Apply(reducer, Delete("owner", 3, 3));
        var scene = Scene(out var logger);
        SceneCommitApplicator.Apply(
            scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1", "owner"),
            logger);
        var plan = AetherFrame.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = new AetherScratch
        {
            Reducer = reducer,
            Inbox = [],
            EntityId = "entity-1",
            LabelFieldId = "label",
            PublicationId = "catalog",
            SceneEntityKey = "entity-1",
            LabelPropertyName = nameof(TestStubEntity.Label),
        };

        plan.Tick(ref scratch, ref runtime);

        var remove = Assert.Single(scratch.SceneCommits!);
        Assert.IsType<RemoveRegisteredEntityCommitRequest>(remove);
        SceneCommitApplicator.Apply(scene, remove, logger);
        Assert.Null(scene.GetEntity("entity-1"));
        Assert.True(reducer.IsDeleted("entity-1"));
        Assert.Equal("kept", reducer.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void Snapshot_and_log_restore_the_delete_envelope_and_frontier()
    {
        using var directory = new TempDir();
        var archive = new AetherArchive(directory.Path);
        var reducer = Store();
        Apply(reducer, Create("owner", 1, 1));
        archive.Save(reducer);
        var applied = reducer.Apply(Delete("owner", 2, 2), observeClock: true);
        archive.Append(applied);

        var fromLog = archive.Load();
        Assert.True(fromLog.IsDeleted("entity-1"));
        Assert.Equal(Assert.Single(reducer.Deletions), Assert.Single(fromLog.Deletions));
        Assert.Equal(2, Assert.Single(fromLog.ActorCursors, cursor => cursor.ActorId == Actor("owner")).Sequence);

        archive.Save(reducer);
        var fromSnapshot = archive.Load();
        Assert.True(fromSnapshot.IsDeleted("entity-1"));
        Assert.Equal(Assert.Single(reducer.Deletions), Assert.Single(fromSnapshot.Deletions));
    }

    [Fact]
    public void Snapshot_cannot_add_a_tombstone_without_its_owner_claim()
    {
        var reducer = Store();
        Apply(reducer, Create("owner", 1, 1));
        var clock = reducer.Clock;
        var image = new AetherImage(
            100,
            [],
            [],
            Deleted: ["entity-1"],
            Entities: reducer.Entities);

        var error = Assert.Throws<AetherProtocolException>(() => reducer.MergeImage(image));

        Assert.Equal("Snapshot tombstone metadata is incomplete.", error.Message);
        Assert.Equal(clock, reducer.Clock);
        Assert.False(reducer.IsDeleted("entity-1"));
        Assert.Empty(reducer.Deletions);
    }

    private static AetherReducer Store()
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        return reducer;
    }

    private static AetherApplied Apply(AetherReducer reducer, AetherOperation operation) =>
        reducer.Apply(operation, observeClock: true);

    private static AetherOperation Create(string owner, long sequence, long lamport) => new(
        Actor(owner) + "/" + sequence,
        "entity-1",
        owner,
        Actor(owner),
        sequence,
        lamport,
        new Dictionary<string, FieldValue>(),
        AetherEffect.CreateEntity,
        entityTypeId: TestStubEntity.RegistryTypeId,
        creatorId: owner,
        ownerId: owner);

    private static AetherOperation Delete(string writer, long sequence, long lamport) => new(
        Actor(writer) + "/" + sequence,
        "entity-1",
        writer,
        Actor(writer),
        sequence,
        lamport,
        new Dictionary<string, FieldValue>(),
        AetherEffect.DeleteEntity);

    private static AetherOperation Write(string writer, long sequence, long lamport, string value) => new(
        Actor(writer) + "/" + sequence,
        "entity-1",
        writer,
        Actor(writer),
        sequence,
        lamport,
        new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label(value) });

    private static string Actor(string principal) => AetherActorId.Format(principal, "session");

    private static ISceneManager Scene(out ILogger logger)
    {
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        logger = services.GetRequiredService<ILogger>();
        return services.GetRequiredService<ISceneManager>();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-delete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
