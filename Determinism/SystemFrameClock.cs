namespace Ape.Core.Determinism;

/// <summary>Default <see cref="IFrameClock"/> backed by <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemFrameClock : IFrameClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
