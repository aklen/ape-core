namespace Ape.Core.Determinism;

/// <summary>
/// Pure-style processor for one batch: same <see cref="FrameContext"/> + same ordered ingress → same result (modulo documented float/time rules).
/// </summary>
public interface IFrameProcessor<TIn, TOut>
{
    /// <summary>
    /// Process all items drained for this frame. Implementations should not perform scene I/O directly—return <see cref="ISceneCommitRequest"/> instances instead.
    /// </summary>
    ProcessingResult<TOut> Process(in FrameContext context, IReadOnlyList<IngressEnvelope<TIn>> batch);
}
