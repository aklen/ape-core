namespace Ape.Core.Determinism;

/// <summary>
/// Optional base for module/plugin types: override <see cref="OnProcessFrame"/> instead of implementing <see cref="IFrameProcessor{TIn,TOut}"/> directly.
/// </summary>
public abstract class DeterministicFrameLayerBase<TIn, TOut> : IFrameProcessor<TIn, TOut>
{
    /// <inheritdoc />
    public ProcessingResult<TOut> Process(in FrameContext context, IReadOnlyList<IngressEnvelope<TIn>> batch) =>
        OnProcessFrame(in context, batch);

    /// <summary>Implement deterministic batch logic for this layer.</summary>
    protected abstract ProcessingResult<TOut> OnProcessFrame(
        in FrameContext context,
        IReadOnlyList<IngressEnvelope<TIn>> batch);
}
