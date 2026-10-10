using System.Collections.ObjectModel;
using System.Text;

namespace Ape.Core.Aether;

/// <summary>
/// Bounded input queue in front of one reducer. Local and remote writes drain through the same loop.
/// Saving and appending stay outside that loop. Results are returned to the caller and are not kept here.
/// </summary>
public sealed class AetherHost
{
    private const int BaseBytes = 1;

    private readonly int _maxBytes;
    private readonly int _maxCount;
    private readonly Queue<Pending> _inbox = new();
    private readonly Queue<Pending> _outbox = new();
    private int _inboxBytes;
    private int _outboxBytes;
    private long _nextRequestId = 1;

    public AetherHost(AetherReducer reducer, int maxBytes, int maxCount)
    {
        Reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        _maxBytes = maxBytes;
        _maxCount = maxCount;
    }

    public AetherReducer Reducer { get; }

    public int PendingBytes => _inboxBytes + _outboxBytes;

    public int PendingCount => _inbox.Count + _outbox.Count;

    public AetherAdmit TryAcceptRemote(AetherOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return TryEnqueue(new Pending(op, null, Measure(op)));
    }

    public AetherAdmit TryAcceptLocal(
        string entityId,
        string writerId,
        string actorId,
        IReadOnlyDictionary<string, FieldValue> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var stored = new Dictionary<string, FieldValue>(changes, StringComparer.Ordinal);
        var bytes = BaseBytes + Utf8(entityId) + Utf8(writerId) + Utf8(actorId);
        foreach (var (fieldId, value) in stored)
            bytes += Utf8(fieldId) + Utf8(value.Text) + 8;
        return TryEnqueue(new Pending(null, new LocalWrite(entityId, writerId, actorId, stored), bytes));
    }

    public IReadOnlyList<AetherOutcome> Drain() => Drain(beforeFold: null);

    internal IReadOnlyList<AetherOutcome> Drain(Action<long>? beforeFold, Action? afterLocalStamp = null)
    {
        var outcomes = new List<AetherOutcome>(_inbox.Count);
        while (_inbox.Count > 0)
        {
            var pending = _inbox.Peek();
            bool foldedOk;
            AetherApplied folded = default;
            string? reason = null;
            try
            {
                beforeFold?.Invoke(pending.RequestId);
                foldedOk = TryFold(pending, afterLocalStamp, out folded, out reason);
            }
            catch (Exception ex) when (ex is not AetherInterruptedException && outcomes.Count > 0)
            {
                throw new AetherInterruptedException(outcomes, ex);
            }

            if (!foldedOk)
            {
                _inbox.Dequeue();
                _inboxBytes -= pending.Bytes;
                outcomes.Add(AetherOutcome.Rejected(pending.RequestId, reason!));
                continue;
            }

            _inbox.Dequeue();
            _inboxBytes -= pending.Bytes;
            _outbox.Enqueue(pending.With(folded));
            _outboxBytes += pending.Bytes;
            outcomes.Add(AetherOutcome.Applied(pending.RequestId, folded));
        }

        return outcomes;
    }

    public IReadOnlyList<AetherDurable> Commit(AetherArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return Commit(archive.Append);
    }

    internal IReadOnlyList<AetherDurable> Commit(Action<AetherApplied> append)
    {
        ArgumentNullException.ThrowIfNull(append);
        var saved = new List<AetherDurable>(_outbox.Count);
        while (_outbox.Count > 0)
        {
            var pending = _outbox.Peek();
            try
            {
                append(pending.Applied!.Value);
            }
            catch (Exception ex) when (ex is not AetherInterruptedException && saved.Count > 0)
            {
                throw new AetherInterruptedException(saved, ex);
            }

            _outbox.Dequeue();
            _outboxBytes -= pending.Bytes;
            saved.Add(new AetherDurable(pending.RequestId));
        }

        return saved;
    }

    public IReadOnlyList<AetherDurable> Save(AetherArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var saved = new List<AetherDurable>(_outbox.Count);
        foreach (var pending in _outbox)
            saved.Add(new AetherDurable(pending.RequestId));
        archive.Save(Reducer);
        _outbox.Clear();
        _outboxBytes = 0;
        return saved;
    }

    public static int Measure(AetherOperation op)
    {
        var bytes = BaseBytes + Utf8(op.Id) + Utf8(op.EntityId) + Utf8(op.WriterId) + Utf8(op.ActorId) + Utf8(op.PublicationId) + 16;
        foreach (var (fieldId, value) in op.Changes)
            bytes += Utf8(fieldId) + Utf8(value.Text) + 8;
        return bytes;
    }

    private AetherAdmit TryEnqueue(Pending pending)
    {
        if (_inbox.Count + _outbox.Count >= _maxCount)
            return AetherAdmit.Full;
        if (pending.Bytes > _maxBytes || _inboxBytes + _outboxBytes + pending.Bytes > _maxBytes)
            return AetherAdmit.Full;
        if (_nextRequestId == long.MaxValue)
            throw new InvalidOperationException("Request ids are exhausted.");

        var requestId = _nextRequestId++;
        pending.Assign(requestId);
        _inbox.Enqueue(pending);
        _inboxBytes += pending.Bytes;
        return AetherAdmit.Queued(requestId);
    }

    private bool TryFold(Pending pending, Action? afterLocalStamp, out AetherApplied folded, out string? reason)
    {
        reason = null;
        if (pending.Remote is not null)
        {
            if (!AetherReducer.HasActorSequence(pending.Remote))
            {
                folded = default;
                reason = "Actor sequence is missing.";
                return false;
            }

            try
            {
                folded = Reducer.Apply(pending.Remote, observeClock: true);
            }
            catch (AetherProtocolException ex)
            {
                folded = default;
                reason = ex.Message;
                return false;
            }
        }
        else
        {
            var stamped = pending.Stamped;
            if (stamped is null)
            {
                var write = pending.Local!;
                try
                {
                    stamped = Reducer.StampLocal(write.EntityId, write.WriterId, write.ActorId, write.Changes);
                }
                catch (AetherProtocolException ex)
                {
                    folded = default;
                    reason = ex.Message;
                    return false;
                }

                pending.Stamped = stamped;
                afterLocalStamp?.Invoke();
            }

            folded = Reducer.Apply(stamped, observeClock: false);
        }

        Reducer.RestoreActorSequence(folded.Operation.ActorId, folded.Operation.Sequence);
        pending.Stamped = null;
        return true;
    }

    private static int Utf8(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private sealed class LocalWrite(string entityId, string writerId, string actorId, Dictionary<string, FieldValue> changes)
    {
        public string EntityId { get; } = entityId;
        public string WriterId { get; } = writerId;
        public string ActorId { get; } = actorId;
        public Dictionary<string, FieldValue> Changes { get; } = changes;
    }

    private sealed class Pending
    {
        public Pending(AetherOperation? remote, LocalWrite? local, int bytes)
        {
            Remote = remote;
            Local = local;
            Bytes = bytes;
        }

        public long RequestId { get; private set; }
        public AetherOperation? Remote { get; }
        public LocalWrite? Local { get; }
        public AetherOperation? Stamped { get; set; }
        public AetherApplied? Applied { get; private set; }
        public int Bytes { get; }

        public void Assign(long requestId) => RequestId = requestId;

        public Pending With(AetherApplied applied)
        {
            Applied = applied;
            return this;
        }
    }
}

public enum AetherAdmitKind : byte
{
    Queued = 1,
    Full = 2,
}

/// <summary>A request entered the queue, or the queue refused it. Refusal is not a protocol rejection.</summary>
public readonly record struct AetherAdmit(AetherAdmitKind Kind, long RequestId)
{
    public static AetherAdmit Full => new(AetherAdmitKind.Full, 0);

    public static AetherAdmit Queued(long requestId) => new(AetherAdmitKind.Queued, requestId);
}

public enum AetherOutcomeKind : byte
{
    Applied = 1,
    Rejected = 2,
}

/// <summary>
/// One drained request. Applied means the reducer took it. It does not mean the archive has it.
/// </summary>
public readonly record struct AetherOutcome(long RequestId, AetherOutcomeKind Kind, string? Reason, AetherApplied? Result)
{
    public static AetherOutcome Applied(long requestId, AetherApplied applied) =>
        new(requestId, AetherOutcomeKind.Applied, null, applied);

    public static AetherOutcome Rejected(long requestId, string reason) =>
        new(requestId, AetherOutcomeKind.Rejected, reason, null);
}

/// <summary>The archive now holds this request, either in the snapshot or in the recovery log.</summary>
public readonly record struct AetherDurable(long RequestId);

/// <summary>
/// A drain or commit stopped after at least one result. Those results are here. The request that failed stays queued.
/// </summary>
public sealed class AetherInterruptedException : Exception
{
    public AetherInterruptedException(IReadOnlyList<AetherOutcome> outcomes, Exception inner)
        : base("The queue stopped after a partial result.", inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Outcomes = Freeze(outcomes);
        Durable = Freeze(Array.Empty<AetherDurable>());
    }

    public AetherInterruptedException(IReadOnlyList<AetherDurable> durable, Exception inner)
        : base("The queue stopped after a partial result.", inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Outcomes = Freeze(Array.Empty<AetherOutcome>());
        Durable = Freeze(durable);
    }

    private static ReadOnlyCollection<T> Freeze<T>(IReadOnlyList<T> items) =>
        new(items.ToArray());

    public IReadOnlyList<AetherOutcome> Outcomes { get; }

    public IReadOnlyList<AetherDurable> Durable { get; }
}
