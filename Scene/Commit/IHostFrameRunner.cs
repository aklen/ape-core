using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Production host tick: the only supported way to advance a live frame (id++, apply, replica tick).
/// Not registered for plugins — they receive <see cref="IFrameParticipantRegistry"/>.
/// </summary>
internal interface IHostFrameRunner
{
    long FrameId { get; }

    HostFrameResult LastResult { get; }

    IReadOnlyList<HostFrameRecord> Records { get; }

    /// <summary>Advances the frame id, runs participants, applies or discards, ticks replicas only on <see cref="HostFrameStatus.Applied"/>.</summary>
    HostFrameResult RunNextFrame(DateTimeOffset? logicalTime = null);
}
