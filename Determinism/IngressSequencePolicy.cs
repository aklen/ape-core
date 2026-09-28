namespace Ape.Core.Determinism;

/// <summary>How source sequences are checked across SAMPLE drains (reconnect / wrap / late packets).</summary>
public enum IngressSequencePolicy
{
    /// <summary>Unique only among currently pending items. CaptureOrder default; drain clears the set.</summary>
    UniqueWithinDrain = 0,

    /// <summary>Each accepted sequence must be greater than the last accepted (same session/epoch).</summary>
    StrictlyIncreasing = 1,

    /// <summary>
    /// Sequences may arrive out of order but must be unique within a bounded recent window
    /// (evicted after <see cref="IngressBuffer{T}.DefaultReorderWindow"/> accepts) until <see cref="IngressBuffer{T}.ResetSession"/>.
    /// </summary>
    UniqueButOutOfOrder = 2,

    /// <summary>
    /// 16-bit serial numbers on the same epoch: <see cref="IngressWrap.IsForward"/>
    /// (modulus <see cref="IngressWrap.Modulus"/>, half-range <see cref="IngressWrap.HalfRange"/>).
    /// Identity uses an extended sequence so wrap is not a duplicate of the previous cycle.
    /// Reconnect is <see cref="IngressBuffer{T}.ResetSession"/>, not a wrap.
    /// </summary>
    WrappingCounter = 3
}
