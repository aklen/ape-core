using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Test / Core-internal collection surface. Production ticks go through <see cref="IHostFrameRunner.RunNextFrame"/>.
/// Not registered in plugin DI.
/// </summary>
internal interface IDeterministicHostTick
{
    void Register(IDeterministicFrameParticipant participant);

    /// <summary>
    /// Invokes each participant on this thread with a sealed-after-return <see cref="IFrameCommitBatch"/>.
    /// Applies concatenated batches only if every participant succeeded and multi-writer policy allows.
    /// Does not tick replicas and does not advance <see cref="IHostFrameRunner.FrameId"/>.
    /// </summary>
    /// <returns><c>true</c> if the frame was applied (including empty batches); <c>false</c> if discarded or failed.</returns>
    bool RaiseHostFrame(long frameId, DateTimeOffset? logicalTime = null);
}
