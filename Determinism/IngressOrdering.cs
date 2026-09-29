namespace Ape.Core.Determinism;

/// <summary>How <see cref="IngressBuffer{T}.DrainOrdered"/> ranks pending items.</summary>
public enum IngressOrdering
{
    /// <summary>
    /// Enqueue order of this live run (auto sequence, or caller <c>explicitSequence</c> if provided).
    /// The captured log is replayable; independent live runs may differ.
    /// </summary>
    CaptureOrder = 0,

    /// <summary>Caller must supply a source sequence; drain sorts by that key.</summary>
    SourceSequence = 1,

    /// <summary>Caller must supply observation time; drain sorts by time, then sequence.</summary>
    ObservationTime = 2
}
