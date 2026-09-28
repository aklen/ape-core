namespace Ape.Core.Determinism;

/// <summary>
/// Deterministic logical time for a processing frame. Replay must pass the same instant that SAMPLE recorded.
/// Epoch and frame duration belong in the journal header — changing tick Hz without recording them breaks replay.
/// Pause and single-step are frame inputs (or journaled host state), not <see cref="DateTime.UtcNow"/> side effects.
/// Do not use <see cref="DateTime.UtcNow"/> inside reducers.
/// </summary>
public static class LogicalFrameTime
{
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan DefaultFrameDuration = TimeSpan.FromMilliseconds(100);

    public const int DefaultTickMilliseconds = 100;

    public static DateTimeOffset FromFrameId(long frameId, int tickMilliseconds = DefaultTickMilliseconds) =>
        FromFrame(frameId, Epoch, TimeSpan.FromMilliseconds(tickMilliseconds));

    public static DateTimeOffset FromFrame(long frameId, DateTimeOffset logicalEpoch, TimeSpan frameDuration)
    {
        if (frameDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameDuration));
        return logicalEpoch + Multiply(frameDuration, frameId);
    }

    private static TimeSpan Multiply(TimeSpan duration, long frameId)
    {
        var ticks = checked(duration.Ticks * frameId);
        return TimeSpan.FromTicks(ticks);
    }
}
