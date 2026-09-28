namespace Ape.Core.Determinism;

/// <summary>
/// Thread-safe ingress backed by a list. <see cref="DrainOrdered"/> ranks by <see cref="Ordering"/>.
/// Identity is stream + <see cref="SessionEpoch"/> + sequence. Named epochs are never reused.
/// Protocol/fusion orderings (<see cref="IngressOrdering.SourceSequence"/>, <see cref="IngressOrdering.ObservationTime"/>)
/// require <c>sourceEpoch</c> on every enqueue. <see cref="IngressOrdering.CaptureOrder"/> may omit it.
/// </summary>
public sealed class IngressBuffer<T> : IIngress<T>
{
    public const int DefaultReorderWindow = 4096;

    private readonly object _lock = new();
    private readonly List<PendingItem> _pending = new();
    private readonly HashSet<long> _pendingSequences = new();
    private readonly BoundedSequenceSet _recentAccepted;
    private readonly HashSet<string> _retiredEpochs = new(StringComparer.Ordinal);
    private long _nextAutoSequence;
    private long? _lastAccepted;
    private long? _lastExtended;
    private string? _sessionEpoch;
    private int _droppedStaleEpoch;

    public IngressBuffer()
        : this(IngressOrdering.CaptureOrder)
    {
    }

    public IngressBuffer(IngressOrdering ordering)
        : this(ordering, DefaultPolicy(ordering))
    {
    }

    public IngressBuffer(IngressOrdering ordering, IngressSequencePolicy sequencePolicy)
        : this(ordering, sequencePolicy, DefaultReorderWindow)
    {
    }

    public IngressBuffer(IngressOrdering ordering, IngressSequencePolicy sequencePolicy, int reorderWindow)
    {
        if (reorderWindow < 1)
            throw new ArgumentOutOfRangeException(nameof(reorderWindow));
        Ordering = ordering;
        SequencePolicy = sequencePolicy;
        _recentAccepted = new BoundedSequenceSet(reorderWindow);
    }

    public IngressOrdering Ordering { get; }

    public IngressSequencePolicy SequencePolicy { get; }

    /// <summary>Session/reconnect epoch. Identity is Source stream + this epoch + sequence.</summary>
    public string? SessionEpoch => _sessionEpoch;

    /// <summary>Packets whose <c>sourceEpoch</c> was retired or not the current session.</summary>
    public int DroppedStaleEpochCount => _droppedStaleEpoch;

    public void Enqueue(
        T payload,
        long? explicitSequence = null,
        DateTime? sourceObservationTime = null,
        string? sourceEpoch = null)
    {
        lock (_lock)
        {
            RequireEpochIfProtocol(sourceEpoch);
            if (!TryBindOrAcceptEpoch(sourceEpoch))
                return;

            long seq;
            switch (Ordering)
            {
                case IngressOrdering.SourceSequence:
                    if (explicitSequence is null)
                    {
                        throw new ArgumentException(
                            "IngressOrdering.SourceSequence requires explicitSequence (protocol/source sequence).",
                            nameof(explicitSequence));
                    }

                    seq = explicitSequence.Value;
                    break;
                case IngressOrdering.ObservationTime:
                    if (sourceObservationTime is null)
                    {
                        throw new ArgumentException(
                            "IngressOrdering.ObservationTime requires sourceObservationTime.",
                            nameof(sourceObservationTime));
                    }

                    if (explicitSequence is null)
                    {
                        throw new ArgumentException(
                            "IngressOrdering.ObservationTime requires explicitSequence as tie-break (unique per source).",
                            nameof(explicitSequence));
                    }

                    seq = explicitSequence.Value;
                    break;
                default:
                    if (explicitSequence is not null)
                    {
                        throw new ArgumentException(
                            "IngressOrdering.CaptureOrder assigns sequence internally. Use SourceSequence for protocol keys.",
                            nameof(explicitSequence));
                    }

                    seq = ++_nextAutoSequence;
                    sourceObservationTime = null;
                    break;
            }

            var extended = RejectAndExtend(seq);

            if (!_pendingSequences.Add(seq))
            {
                throw new InvalidOperationException(
                    $"Duplicate ingress sequence {seq} in the current pending batch.");
            }

            _pending.Add(new PendingItem(new IngressEnvelope<T>(payload, seq, sourceObservationTime), extended));
        }
    }

    /// <summary>
    /// Reconnect / sensor restart: new epoch, drop pending input, clear sequence state.
    /// Wrap of a protocol counter is not this method — use <see cref="IngressSequencePolicy.WrappingCounter"/>.
    /// </summary>
    public void ResetSession(string? epoch = null)
    {
        lock (_lock)
        {
            DropPendingUnlocked();
            if (_sessionEpoch != null)
                _retiredEpochs.Add(_sessionEpoch);

            if (epoch != null && _retiredEpochs.Contains(epoch))
            {
                throw new InvalidOperationException(
                    $"Ingress epoch '{epoch}' cannot be reused on this stream.");
            }

            _sessionEpoch = epoch;
            _lastAccepted = null;
            _lastExtended = null;
            _recentAccepted.Clear();
            _nextAutoSequence = 0;
        }
    }

    public IReadOnlyList<IngressEnvelope<T>> DrainOrdered()
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
                return Array.Empty<IngressEnvelope<T>>();

            var copy = _pending.ToArray();
            _pending.Clear();
            _pendingSequences.Clear();
            Array.Sort(copy, ComparePending);

            var envelopes = new IngressEnvelope<T>[copy.Length];
            for (var i = 0; i < copy.Length; i++)
            {
                RememberAccepted(copy[i]);
                envelopes[i] = copy[i].Envelope;
            }

            return envelopes;
        }
    }

    private void RequireEpochIfProtocol(string? sourceEpoch)
    {
        if (Ordering == IngressOrdering.CaptureOrder)
            return;
        if (string.IsNullOrWhiteSpace(sourceEpoch))
        {
            throw new ArgumentException(
                $"{Ordering} requires sourceEpoch (session/connection id). CaptureOrder may omit it.",
                nameof(sourceEpoch));
        }
    }

    private bool TryBindOrAcceptEpoch(string? sourceEpoch)
    {
        if (sourceEpoch is null)
            return true;

        if (_retiredEpochs.Contains(sourceEpoch))
        {
            _droppedStaleEpoch++;
            return false;
        }

        if (_sessionEpoch is null)
        {
            _sessionEpoch = sourceEpoch;
            return true;
        }

        if (!string.Equals(_sessionEpoch, sourceEpoch, StringComparison.Ordinal))
        {
            _droppedStaleEpoch++;
            return false;
        }

        return true;
    }

    private void DropPendingUnlocked()
    {
        _pending.Clear();
        _pendingSequences.Clear();
    }

    private static IngressSequencePolicy DefaultPolicy(IngressOrdering ordering) =>
        ordering == IngressOrdering.CaptureOrder
            ? IngressSequencePolicy.UniqueWithinDrain
            : IngressSequencePolicy.StrictlyIncreasing;

    private long RejectAndExtend(long seq)
    {
        switch (SequencePolicy)
        {
            case IngressSequencePolicy.UniqueWithinDrain:
                return seq;
            case IngressSequencePolicy.StrictlyIncreasing:
                if (_lastAccepted is long last && seq <= last)
                {
                    throw new InvalidOperationException(
                        $"Ingress sequence {seq} is not strictly increasing (last accepted {last}, epoch={_sessionEpoch ?? "-"}).");
                }

                return seq;
            case IngressSequencePolicy.UniqueButOutOfOrder:
                if (_recentAccepted.Contains(seq))
                {
                    throw new InvalidOperationException(
                        $"Ingress sequence {seq} already accepted this session (epoch={_sessionEpoch ?? "-"}).");
                }

                return seq;
            case IngressSequencePolicy.WrappingCounter:
                IngressWrap.Validate(seq);
                if (OriginExtended() is long origin)
                {
                    var originRaw = IngressWrap.Raw(origin);
                    if (!IngressWrap.IsForward(originRaw, seq))
                    {
                        throw new InvalidOperationException(
                            $"Ingress sequence {seq} is not a wrap-forward step (last raw={originRaw}, modulus={IngressWrap.Modulus}, half-range={IngressWrap.HalfRange}).");
                    }

                    return IngressWrap.Extend(origin, seq);
                }

                return seq;
            default:
                return seq;
        }
    }

    private long? OriginExtended()
    {
        if (_pending.Count > 0)
        {
            var max = _pending[0].Extended;
            for (var i = 1; i < _pending.Count; i++)
            {
                if (_pending[i].Extended > max)
                    max = _pending[i].Extended;
            }

            return max;
        }

        return _lastExtended;
    }

    private void RememberAccepted(PendingItem item)
    {
        switch (SequencePolicy)
        {
            case IngressSequencePolicy.UniqueWithinDrain:
                return;
            case IngressSequencePolicy.StrictlyIncreasing:
                _lastAccepted = _lastAccepted is long last
                    ? Math.Max(last, item.Envelope.Sequence)
                    : item.Envelope.Sequence;
                return;
            case IngressSequencePolicy.UniqueButOutOfOrder:
                _recentAccepted.Add(item.Envelope.Sequence);
                return;
            case IngressSequencePolicy.WrappingCounter:
                _lastExtended = item.Extended;
                _lastAccepted = item.Envelope.Sequence;
                return;
        }
    }

    private int ComparePending(PendingItem a, PendingItem b)
    {
        if (SequencePolicy == IngressSequencePolicy.WrappingCounter)
            return a.Extended.CompareTo(b.Extended);

        if (Ordering == IngressOrdering.ObservationTime)
        {
            var ta = a.Envelope.SourceObservationTime ?? DateTime.MinValue;
            var tb = b.Envelope.SourceObservationTime ?? DateTime.MinValue;
            var byTime = ta.CompareTo(tb);
            if (byTime != 0)
                return byTime;
        }

        return a.Envelope.Sequence.CompareTo(b.Envelope.Sequence);
    }

    private readonly record struct PendingItem(IngressEnvelope<T> Envelope, long Extended);

    /// <summary>FIFO unique set so UniqueButOutOfOrder cannot grow without bound.</summary>
    private sealed class BoundedSequenceSet
    {
        private readonly int _capacity;
        private readonly Queue<long> _order = new();
        private readonly HashSet<long> _set = new();

        public BoundedSequenceSet(int capacity) => _capacity = capacity;

        public bool Contains(long seq) => _set.Contains(seq);

        public void Add(long seq)
        {
            if (!_set.Add(seq))
                return;
            _order.Enqueue(seq);
            while (_order.Count > _capacity)
                _set.Remove(_order.Dequeue());
        }

        public void Clear()
        {
            _order.Clear();
            _set.Clear();
        }
    }
}
