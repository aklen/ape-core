namespace Ape.Core.Determinism;

/// <summary>
/// One ingress item with stable ordering key and optional source observation time.
/// </summary>
public readonly record struct IngressEnvelope<T>(
    T Payload,
    long Sequence,
    DateTime? SourceObservationTime);
