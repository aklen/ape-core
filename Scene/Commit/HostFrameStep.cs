using Ape.Core.Replication;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Test helper: id++ → RaiseHostFrame → replica tick only on apply.
/// Production uses <see cref="IHostFrameRunner.RunNextFrame"/> so this pairing cannot be skipped.
/// </summary>
internal static class HostFrameStep
{
    /// <returns>Whether the frame was applied (same as <see cref="IDeterministicHostTick.RaiseHostFrame"/>).</returns>
    public static bool Run(
        IDeterministicHostTick host,
        ref long hostFrameId,
        IReplicaManager? replicaManager,
        bool enableNetwork)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host is IHostFrameRunner runner)
        {
            if (host is SceneCommitService service)
                service.SetReplication(replicaManager, enableNetwork);
            var result = runner.RunNextFrame();
            hostFrameId = result.FrameId;
            return result.Status == HostFrameStatus.Applied;
        }

        hostFrameId++;
        var applied = host.RaiseHostFrame(hostFrameId);
        if (enableNetwork && applied)
            replicaManager?.Tick();
        return applied;
    }
}
