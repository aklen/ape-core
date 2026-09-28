using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Ape.Core.Determinism;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

/// <summary>
/// One <see cref="SceneCommitService.RaiseHostFrame"/> with fixed participants → golden invariant snapshot (SHA256) of resulting node state.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SceneCommitServiceTickGoldenTests
{
    /// <summary>SHA256 of <c>FormattableString.Invariant($"golden|{pos.X:R9}|{pos.Y:R9}|{pos.Z:R9}")</c> — with float R9, values format as <c>11</c>, <c>22</c>, <c>33</c>.</summary>
    private const string GoldenTick1NodePositionSha256Hex =
        "996CA1ED495A4014E251D7A250297A979C3CA36E1859C5DB9886319009D994CD";

    private sealed class GoldenTickParticipant : IDeterministicFrameParticipant
    {
        public string ParticipantId => "golden";
        public FramePhase Phase => FramePhase.Transform;
        public int Order => 100;

        public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
        {
            commits.Enqueue(new CreateSceneNodeCommitRequest("golden", "owner", Vector3.Zero));
            commits.Enqueue(new SetSceneNodePositionCommitRequest("golden", new Vector3(11f, 22f, 33f)));
        }
    }

    [Fact]
    public void Single_host_tick_produces_golden_invariant_position_hash()
    {
        var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
        var scene = sp.GetRequiredService<ISceneManager>();
        var host = sp.GetRequiredService<SceneCommitService>();

        host.Register(new GoldenTickParticipant());
        host.RaiseHostFrame(42L);

        var node = scene.GetNode("golden");
        Assert.NotNull(node);
        Assert.Equal(new Vector3(11f, 22f, 33f), node!.Position);

        var inv = FormattableString.Invariant($"golden|{node.Position.X:R9}|{node.Position.Y:R9}|{node.Position.Z:R9}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inv)));
        Assert.Equal(GoldenTick1NodePositionSha256Hex, hash);
    }

    [Fact]
    public void Same_tick_twice_on_two_fresh_pipelines_matches_golden_hash()
    {
        static string RunHash()
        {
            var sp = SceneIntegrationServices.Build(includeSceneCommitPipeline: true);
            var scene = sp.GetRequiredService<ISceneManager>();
            var host = sp.GetRequiredService<SceneCommitService>();
            host.Register(new GoldenTickParticipant());
            host.RaiseHostFrame(99L);
            var node = scene.GetNode("golden")!;
            var inv = FormattableString.Invariant($"golden|{node.Position.X:R9}|{node.Position.Y:R9}|{node.Position.Z:R9}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inv)));
        }

        Assert.Equal(GoldenTick1NodePositionSha256Hex, RunHash());
        Assert.Equal(RunHash(), RunHash());
    }
}
