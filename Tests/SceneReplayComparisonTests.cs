using System.Numerics;
using System.Text.Json;
using Ape.Core.Determinism;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Core.Tests;

public sealed class SceneReplayComparisonTests
{
    private sealed record ReplayStep(
        long FrameId, string Command, float[]? Position, string Status, string Digest);
    private sealed record CommandPayload(string Command, float[]? Position);

    private sealed class ScriptedParticipant(IReadOnlyDictionary<long, SampleFrame> samples)
        : IDeterministicFrameParticipant
    {
        public string ParticipantId => "scene-replay-script";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 0;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            var sample = samples[context.FrameId];
            if (sample.LogicalTime != context.LogicalTime)
                throw new InvalidDataException("Replay frame logical time mismatch.");
            var input = Assert.Single(sample.Inputs);
            if (input.PayloadType != "scene-command-v1" || input.Payload is not { } bytes)
                throw new InvalidDataException("Missing Scene replay command payload.");
            var command = JsonSerializer.Deserialize<CommandPayload>(bytes.Span)
                ?? throw new InvalidDataException("Invalid Scene replay command payload.");

            switch (command.Command)
            {
                case "create":
                    commits.Enqueue(new CreateSceneNodeCommitRequest("cube", "owner", Position(command)));
                    break;
                case "move":
                    commits.Enqueue(new SetSceneNodePropertyCommitRequest("cube", "Position", Position(command)));
                    break;
                case "fail":
                    commits.Enqueue(new SetSceneNodePropertyCommitRequest("cube", "Position", new Vector3(99)));
                    throw new InvalidOperationException("recorded failure");
                default:
                    throw new InvalidDataException($"Unknown replay command '{command.Command}'.");
            }
        }

        private static Vector3 Position(CommandPayload command)
        {
            if (command.Position is not { Length: 3 } xyz)
                throw new InvalidDataException("Replay position needs three coordinates.");
            return new Vector3(xyz[0], xyz[1], xyz[2]);
        }
    }

    [Fact]
    public void Tiny_script_replays_same_scene_sequence_on_two_fresh_hosts_and_matches_golden()
    {
        var script = LoadFixture();
        var first = RunCold(script);
        var second = RunCold(script);
        var expected = script.Select(s => $"{s.FrameId}|{s.Status}|{s.Digest}").ToArray();

        Assert.Equal(first, second);
        Assert.True(expected.SequenceEqual(first),
            $"Scene replay mismatch:\n{string.Join('\n', first)}");
        Assert.Equal(first[0].Split('|')[2], first[1].Split('|')[2]);
    }

    [Fact]
    public void Changed_replayed_property_is_detected_by_scene_digest()
    {
        var script = LoadFixture();
        var changed = script.Select(s => s.FrameId == 3
            ? s with { Position = [4, 5, 7] }
            : s).ToArray();

        var baseline = RunCold(script);
        var divergent = RunCold(changed);

        Assert.Equal(baseline[0], divergent[0]);
        Assert.Equal(baseline[1], divergent[1]);
        Assert.NotEqual(baseline[2], divergent[2]);
    }

    [Fact]
    public void Scene_digest_covers_registered_entity_payloads_as_well_as_nodes()
    {
        static (string Before, string After) RunFresh()
        {
            var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
            var scene = services.GetRequiredService<ISceneManager>();
            var logger = services.GetRequiredService<ILogger>();
            SceneCommitApplicator.Apply(scene,
                new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "track"), logger);
            var before = SceneStateDigest.Capture(scene);
            SceneCommitApplicator.Apply(scene,
                new SetSceneEntityPropertyCommitRequest("track", nameof(TestStubEntity.Counter), 7), logger);
            return (before, SceneStateDigest.Capture(scene));
        }

        var first = RunFresh();
        Assert.NotEqual(first.Before, first.After);
        Assert.Equal(first, RunFresh());
    }

    [Fact]
    public void Scene_digest_does_not_depend_on_node_registration_order()
    {
        static string Run(params string[] names)
        {
            var services = SceneIntegrationServices.Build();
            var scene = services.GetRequiredService<ISceneManager>();
            var logger = services.GetRequiredService<ILogger>();
            foreach (var name in names)
                SceneCommitApplicator.Apply(scene,
                    new CreateSceneNodeCommitRequest(name, "owner", Vector3.One), logger);
            return SceneStateDigest.Capture(scene);
        }

        Assert.Equal(Run("a", "b"), Run("b", "a"));
    }

    private static ReplayStep[] LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scene-replay-tiny.jsonl");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<ReplayStep>(line, options)
                ?? throw new InvalidDataException("Empty replay step."))
            .ToArray();
    }

    private static string[] RunCold(IReadOnlyList<ReplayStep> script)
    {
        var samples = PersistAndReadSamples(script);
        var services = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var scene = services.GetRequiredService<ISceneRead>();
        var host = services.GetRequiredService<SceneCommitService>();
        host.Register(new ScriptedParticipant(samples.ToDictionary(s => s.FrameId)));
        var outcomePath = Path.Combine(Path.GetTempPath(), $"ape-scene-outcome-{Guid.NewGuid():N}.jsonl");
        try
        {
            var results = new List<string>();
            using (var writer = HostFrameOutcomeJournal.CreateNew(outcomePath))
            {
                host.SetOutcomeJournal(writer);
                foreach (var sample in samples)
                {
                    var result = host.RunNextFrame();
                    Assert.Equal(sample.FrameId, result.FrameId);
                    results.Add($"{result.FrameId}|{result.Status}|{SceneStateDigest.Capture(scene)}");
                }
                host.SetOutcomeJournal(null);
            }

            var outcomes = HostFrameOutcomeJournal.Read(outcomePath);
            HostFrameOutcomeJournal.VerifyAlignment(samples, outcomes);
            Assert.Equal(results.Select(r => r.Split('|')[1]), outcomes.Select(o => o.Status.ToString()));
            return results.ToArray();
        }
        finally
        {
            File.Delete(outcomePath);
        }
    }

    private static IReadOnlyList<SampleFrame> PersistAndReadSamples(IReadOnlyList<ReplayStep> script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ape-scene-sample-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = SampleFrameJournal.CreateNew(path,
                new SampleFrameJournalHeader(LogicalFrameTime.Epoch, LogicalFrameTime.DefaultFrameDuration)))
            {
                foreach (var step in script)
                {
                    var input = new SampleFrameInput("scene-test", null, null, "scene-command-v1",
                        Payload: JsonSerializer.SerializeToUtf8Bytes(
                            new CommandPayload(step.Command, step.Position)));
                    writer.Append(new SampleFrame(step.FrameId,
                        LogicalFrameTime.FromFrameId(step.FrameId), [input]));
                }
            }
            return SampleFrameJournal.Read(path).Frames;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
