namespace Ape.Core.Determinism;

/// <summary>
/// Wires <see cref="IIngress{T}"/> + <see cref="IFrameProcessor{TIn,TOut}"/> for a single deterministic unit.
/// Host / commit runner calls <see cref="RunFrame"/> once per global frame id.
/// Logical time defaults to <see cref="LogicalFrameTime.FromFrameId"/> so replay of the same frame id is stable.
/// </summary>
public sealed class FrameProcessingHost<TIn, TOut>
{
    private readonly IIngress<TIn> _ingress;
    private readonly IFrameProcessor<TIn, TOut> _processor;

    public FrameProcessingHost(
        IFrameProcessor<TIn, TOut> processor,
        IIngress<TIn>? ingress = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _ingress = ingress ?? new IngressBuffer<TIn>();
    }

    /// <summary>Ingress for producers (TCP, timers).</summary>
    public IIngress<TIn> Ingress => _ingress;

    /// <summary>Drain ingress, run processor, return combined outputs.</summary>
    public ProcessingResult<TOut> RunFrame(
        long frameId,
        string? policyVersion = null,
        DateTimeOffset? logicalTime = null)
    {
        var batch = _ingress.DrainOrdered();
        var time = logicalTime ?? LogicalFrameTime.FromFrameId(frameId);
        var ctx = new FrameContext(frameId, time, policyVersion);
        return _processor.Process(in ctx, batch);
    }
}
