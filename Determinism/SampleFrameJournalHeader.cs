namespace Ape.Core.Determinism;

/// <summary>
/// Journal prefix so replay reconstructs <see cref="LogicalFrameTime.FromFrame"/> with the same scale.
/// Pause/step belong on the per-frame input/state, not as wall-clock side effects.
/// </summary>
public sealed record SampleFrameJournalHeader(
    DateTimeOffset LogicalEpoch,
    TimeSpan FrameDuration,
    string? TimeScale = null,
    string? ConfigHash = null);
