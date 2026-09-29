using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Scene-write capability for one participant for the duration of a single <see cref="IDeterministicFrameParticipant.OnHostFrame"/> call.
/// Host-thread only; sealed when the call returns. Callbacks must use <see cref="IIngress{T}"/>, not this type.
/// </summary>
public interface IFrameCommitBatch : ISceneCommitSink
{
    string ParticipantId { get; }

    /// <summary>Forbid further <see cref="ISceneCommitSink.Enqueue"/> calls. Idempotent.</summary>
    void Seal();
}
