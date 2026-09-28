namespace Ape.Core.Determinism;

/// <summary>
/// Producer-facing ingress: async I/O and callbacks enqueue here; SAMPLE drains in the host frame.
/// Identity is <c>stream + epoch + sequence</c>. Reconnect is a new epoch, not a counter wrap.
/// </summary>
public interface IIngress<T>
{
    /// <param name="payload">Incoming work item.</param>
    /// <param name="explicitSequence">Source/protocol sequence when the stream uses <see cref="IngressOrdering.SourceSequence"/>.</param>
    /// <param name="sourceObservationTime">When the phenomenon occurred (sensor / wire), not processing time.</param>
    /// <param name="sourceEpoch">
    /// Session/connection id. Required for SourceSequence and ObservationTime.
    /// Stale (retired) epochs are dropped; the current session is bound on first use.
    /// </param>
    void Enqueue(
        T payload,
        long? explicitSequence = null,
        DateTime? sourceObservationTime = null,
        string? sourceEpoch = null);

    /// <summary>
    /// Starts a new session. Pending items are dropped (not drained into SAMPLE).
    /// Named epochs are never reused on the same stream.
    /// </summary>
    void ResetSession(string? epoch = null);

    /// <summary>Drain pending items according to the buffer's <see cref="IngressOrdering"/>.</summary>
    IReadOnlyList<IngressEnvelope<T>> DrainOrdered();
}
