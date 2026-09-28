using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// One ordered unit of SAMPLE/TICK/COMMIT work per host frame.
/// Sort key: <see cref="Phase"/>, then <see cref="Order"/>, then <see cref="ParticipantId"/> (ordinal).
/// Duplicate <see cref="ParticipantId"/> values are a configuration error.
/// </summary>
public interface IDeterministicFrameParticipant
{
    string ParticipantId { get; }

    FramePhase Phase { get; }

    int Order { get; }

    void OnHostFrame(in FrameContext context, IFrameCommitBatch commits);
}
