namespace Ape.Core.Determinism;

/// <summary>
/// One SAMPLE result: the replay input for a processing frame.
/// Large payloads may use <see cref="SampleFrameInput.PayloadHash"/> + <see cref="SampleFrameInput.CasReference"/> instead of inline bytes.
/// </summary>
public sealed record SampleFrame(
    long FrameId,
    DateTimeOffset LogicalTime,
    IReadOnlyList<SampleFrameInput> Inputs);

/// <summary>One ingress item as copied in SAMPLE (not the OS callback itself).</summary>
public sealed record SampleFrameInput(
    string SourceId,
    long? SourceSequence,
    DateTime? ObservationTime,
    string PayloadType,
    string? PayloadVersion = null,
    ReadOnlyMemory<byte>? Payload = null,
    string? PayloadHash = null,
    string? CasReference = null,
    string? SourceEpoch = null);
