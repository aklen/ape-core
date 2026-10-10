using Ape.Core.Aether;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class AetherFrameIntegrationTests
{
    [Fact]
    public void A_queue_drains_across_frames_then_a_save_restarts_into_the_scene()
    {
        using var dir = new TempDir();
        var archive = new AetherArchive(dir.Path);
        var store = NewStore();
        archive.Save(store);
        var logBefore = File.ReadAllBytes(archive.LogPath);
        var snapshotBefore = File.ReadAllBytes(archive.SnapshotPath);

        var host = new AetherHost(store, 4096, 8);
        foreach (var op in Queued())
            Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(op).Kind);

        var plan = AetherFrame.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = new AetherScratch
        {
            Host = host,
            Reducer = store,
            FrameOperationBudget = 1,
            FrameByteBudget = 1_000_000,
            Inbox = new List<AetherOperation> { Label("skip-me", 9, 99, "nope") },
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = "catalog",
            SceneEntityKey = "entity-1",
            LabelPropertyName = nameof(TestStubEntity.Label),
            SumPropertyName = nameof(TestStubEntity.Counter),
        };

        var scene = OpenScene(out var log);
        var seen = new List<(string? Label, int Sum)>();
        for (var frame = 0; frame < 4; frame++)
        {
            plan.Tick(ref scratch, ref runtime);
            ApplyScene(scene, log, scratch.SceneCommits!);
            var stub = Assert.IsType<TestStubEntity>(scene.GetEntity("entity-1"));
            seen.Add((stub.Label, stub.Counter));
        }

        Assert.Equal(
            new (string? Label, int Sum)[]
            {
                (null, 0),
                ("one", 0),
                ("two", 0),
                ("two", 4),
            },
            seen);
        Assert.Equal("two", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(4, store.ResolveSum("entity-1", "offset"));
        Assert.Equal(14, store.Clock);
        Assert.Equal(4, Assert.Single(store.ActorCursors).Sequence);
        Assert.Equal("nope", Assert.Single(scratch.Inbox).Changes["label"].Text);
        Assert.Equal(logBefore, File.ReadAllBytes(archive.LogPath));
        Assert.Equal(snapshotBefore, File.ReadAllBytes(archive.SnapshotPath));
        Assert.Equal(4, host.PendingCount);

        var durable = host.Save(archive);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, durable.Select(item => item.RequestId));
        Assert.Empty(host.Commit(archive));
        Assert.Equal(0, host.PendingCount);

        var loaded = archive.Load();
        var restarted = new AetherHost(loaded, 4096, 8);
        var again = new AetherScratch
        {
            Host = restarted,
            Reducer = loaded,
            FrameOperationBudget = 1,
            FrameByteBudget = 1_000_000,
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = "catalog",
            SceneEntityKey = "entity-1",
            LabelPropertyName = nameof(TestStubEntity.Label),
            SumPropertyName = nameof(TestStubEntity.Counter),
        };
        var restartedRuntime = new PlanRuntime();
        plan.InitRuntime(ref restartedRuntime);
        plan.Tick(ref again, ref restartedRuntime);

        var fresh = OpenScene(out var freshLog);
        ApplyScene(fresh, freshLog, again.SceneCommits!);
        var visible = Assert.IsType<TestStubEntity>(fresh.GetEntity("entity-1"));
        Assert.Equal("two", visible.Label);
        Assert.Equal(4, visible.Counter);
        Assert.Equal(14, loaded.Clock);
        Assert.Equal(4, Assert.Single(loaded.ActorCursors).Sequence);
        Assert.Equal("two", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(4, loaded.ResolveSum("entity-1", "offset"));
    }

    [Fact]
    public void A_tick_keeps_the_rejected_request_and_the_valid_write()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var bad = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.FixedPoint(1),
        });
        var good = host.TryAcceptLocal("entity-1", "peer-A", "peer-A/s1", new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label("ok"),
        });
        var plan = AetherFrame.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = new AetherScratch
        {
            Host = host,
            Reducer = store,
            FrameOperationBudget = 2,
            FrameByteBudget = 1_000_000,
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = "catalog",
        };

        plan.Tick(ref scratch, ref runtime);

        Assert.Equal(2, scratch.Outcomes!.Count);
        Assert.Equal(bad.RequestId, scratch.Outcomes[0].RequestId);
        Assert.Equal(AetherOutcomeKind.Rejected, scratch.Outcomes[0].Kind);
        Assert.Equal("Field 'label' stores a label.", scratch.Outcomes[0].Reason);
        Assert.Equal(good.RequestId, scratch.Outcomes[1].RequestId);
        Assert.Equal(AetherOutcomeKind.Applied, scratch.Outcomes[1].Kind);
        Assert.Equal("ok", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Empty(host.Drain());

        plan.Tick(ref scratch, ref runtime);
        Assert.Empty(scratch.Outcomes);
    }

    [Fact]
    public void A_tick_without_a_scene_target_drops_the_previous_commits()
    {
        var plan = AetherFrame.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = new AetherScratch
        {
            Reducer = NewStore(),
            Inbox = new List<AetherOperation>(),
            EntityId = "entity-1",
            LabelFieldId = "label",
            SumFieldId = "offset",
            PublicationId = "catalog",
            SceneEntityKey = "entity-1",
            LabelPropertyName = nameof(TestStubEntity.Label),
            SumPropertyName = nameof(TestStubEntity.Counter),
        };

        plan.Tick(ref scratch, ref runtime);
        var commits = scratch.SceneCommits;
        Assert.Equal(2, commits!.Count);

        scratch.SceneEntityKey = null;
        plan.Tick(ref scratch, ref runtime);
        Assert.Same(commits, scratch.SceneCommits);
        Assert.Empty(commits);
    }

    private static ISceneManager OpenScene(out ILogger log)
    {
        var sp = RegisteredEntityCommitTests.BuildWithStubRegistered();
        log = sp.GetRequiredService<ILogger>();
        var scene = sp.GetRequiredService<ISceneManager>();
        SceneCommitApplicator.Apply(
            scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1"),
            log);
        return scene;
    }

    private static void ApplyScene(ISceneManager scene, ILogger log, IReadOnlyList<ISceneCommitRequest> commits)
    {
        foreach (var request in commits)
            SceneCommitApplicator.Apply(scene, request, log);
    }

    private static AetherReducer NewStore()
    {
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        store.DefineField("offset", "sumContributions");
        return store;
    }

    private static IEnumerable<AetherOperation> Queued()
    {
        yield return new AetherOperation(
            "pub",
            "entity-1",
            "peer-A",
            "peer-A/s1",
            1,
            10,
            new Dictionary<string, FieldValue>(),
            AetherEffect.Publish,
            "catalog");
        yield return Label("one-id", 2, 11, "one");
        yield return Label("two-id", 3, 12, "two");
        yield return new AetherOperation(
            "sum-id",
            "entity-1",
            "peer-A",
            "peer-A/s1",
            4,
            13,
            new Dictionary<string, FieldValue> { ["offset"] = FieldValue.FixedPoint(4) });
    }

    private static AetherOperation Label(string id, long sequence, long lamport, string text) =>
        new(id, "entity-1", "peer-A", "peer-A/s1", sequence, lamport, new Dictionary<string, FieldValue>
        {
            ["label"] = FieldValue.Label(text),
        });

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
