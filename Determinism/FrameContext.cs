namespace Ape.Core.Determinism;

/// <summary>
/// Immutable context for one processing step. Time is <see cref="LogicalTime"/> (journaled with <see cref="SampleFrame"/>), never wall clock.
/// </summary>
public readonly record struct FrameContext(
    long FrameId,
    DateTimeOffset LogicalTime,
    string? PolicyVersion = null)
{
    /// <summary>Same as <see cref="LogicalTime"/> (name kept for existing processors).</summary>
    public DateTimeOffset ProcessingTime => LogicalTime;
}
