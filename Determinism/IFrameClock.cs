namespace Ape.Core.Determinism;

/// <summary>
/// Injectable wall/observation clock. Reducers use <see cref="FrameContext.LogicalTime"/>, not this type.
/// </summary>
public interface IFrameClock
{
    /// <summary>Wall-clock or simulated instant for the current process.</summary>
    DateTimeOffset UtcNow { get; }
}
