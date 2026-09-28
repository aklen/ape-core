namespace Ape.Core.Determinism;

/// <summary>
/// Output of one <see cref="IFrameProcessor{TIn,TOut}.Process"/> invocation: domain outputs plus optional scene mutations.
/// </summary>
public readonly record struct ProcessingResult<TOut>(
    IReadOnlyList<TOut> Outputs,
    IReadOnlyList<ISceneCommitRequest>? SceneCommits = null)
{
    /// <summary>Empty result.</summary>
    public static ProcessingResult<TOut> Empty { get; } = new(Array.Empty<TOut>(), null);
}
