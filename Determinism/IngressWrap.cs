namespace Ape.Core.Determinism;

/// <summary>
/// 16-bit serial-number arithmetic for <see cref="IngressSequencePolicy.WrappingCounter"/>.
/// Reconnect is a new epoch (<see cref="IngressBuffer{T}.ResetSession"/>), not a wrap.
/// </summary>
public static class IngressWrap
{
    public const long Modulus = 65536;
    public const long HalfRange = 32768;

    /// <summary>Raw protocol counter in <c>[0, Modulus)</c>.</summary>
    public static void Validate(long value)
    {
        if (value < 0 || value >= Modulus)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Sequence must be in [0, {Modulus - 1}].");
        }
    }

    /// <summary>
    /// Forward step: modular delta in <c>(0, HalfRange)</c>.
    /// Delta 0 is a duplicate; delta == HalfRange is ambiguous and rejected.
    /// </summary>
    public static bool IsForward(long previous, long current)
    {
        var delta = ForwardDelta(previous, current);
        return delta > 0 && delta < HalfRange;
    }

    public static long ForwardDelta(long previous, long current)
    {
        Validate(previous);
        Validate(current);
        return (current - previous + Modulus) % Modulus;
    }

    public static long Raw(long extended) => ((extended % Modulus) + Modulus) % Modulus;

    /// <summary>Monotonic extension of <paramref name="currentRaw"/> past <paramref name="previousExtended"/>.</summary>
    public static long Extend(long previousExtended, long currentRaw)
    {
        var previousRaw = Raw(previousExtended);
        var delta = ForwardDelta(previousRaw, currentRaw);
        return previousExtended + delta;
    }
}
