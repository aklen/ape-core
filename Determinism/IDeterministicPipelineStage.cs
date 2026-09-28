namespace Ape.Core.Determinism;

/// <summary>
/// Optional metadata for composing multiple <see cref="IFrameProcessor{TIn,TOut}"/> hosts in a fixed order (pipeline graph node).
/// </summary>
public interface IDeterministicPipelineStage
{
    /// <summary>Stable id for config and logs.</summary>
    string StageId { get; }

    /// <summary>Relative order when no explicit DAG is used (lower runs first).</summary>
    int Order { get; }
}
