namespace Ape.Core.Scene.Commit;

/// <summary>Outcome of one host frame after collection (and apply, if successful).</summary>
public enum HostFrameStatus
{
    /// <summary>Batches applied; replicas may tick.</summary>
    Applied = 0,

    /// <summary>Policy reject (e.g. strict multi-writer). Scene unchanged; SAMPLE/scratch already ran.</summary>
    Discarded = 1,

    /// <summary>A participant or applicator threw. Applicator failure may leave partial Scene writes.</summary>
    Failed = 2
}

/// <summary>
/// After a non-applied frame: default continues (input consumed, next frame runs).
/// Safety pipelines may set <see cref="StopPipeline"/> (<c>modules["Ape.Core.Scene"].failedFrame = "stop"</c>).
/// </summary>
public enum FailedFramePolicy
{
    ConsumeAndContinue = 0,
    StopPipeline = 1
}

/// <summary>Audit row for applied, discarded, and failed frames.</summary>
public sealed record HostFrameRecord(
    long FrameId,
    DateTimeOffset LogicalTime,
    HostFrameStatus Status,
    string? FailureParticipant = null,
    string? FailureReason = null,
    bool MayHavePartialSceneWrites = false);

/// <summary>Result of one production host tick.</summary>
public readonly record struct HostFrameResult(
    long FrameId,
    DateTimeOffset LogicalTime,
    HostFrameStatus Status,
    bool Replicated,
    string? FailureParticipant = null,
    string? FailureReason = null,
    bool MayHavePartialSceneWrites = false);
