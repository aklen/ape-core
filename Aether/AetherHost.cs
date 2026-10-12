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

    /// <summary>
    /// Queues a remote write when its writer and actor peer are <paramref name="principalId"/>.
    /// The socket slot is not a writer. Refusal happens before the queue.
    /// </summary>
    public AetherAdmit TryAcceptRemote(AetherOperation op, string principalId)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (!BindsToPeer(op.WriterId, op.ActorId, principalId))
            return AetherAdmit.Refused("Actor is not on this connection.");
        if (op.Effect == AetherEffect.CreateEntity
            && (!string.Equals(op.CreatorId, principalId, StringComparison.Ordinal)
                || !string.Equals(op.OwnerId, principalId, StringComparison.Ordinal)))
            return AetherAdmit.Refused("Create is not owned by this connection.");
        if (op.Effect == AetherEffect.DeleteEntity)
        {
            var entity = Reducer.GetEntity(op.EntityId);
            if (entity is null)
                return AetherAdmit.Refused("Delete entity owner is unknown.");
            if (!string.Equals(entity.OwnerId, principalId, StringComparison.Ordinal))
                return AetherAdmit.Refused("Delete is not issued by the entity owner.");
        }
        return TryEnqueue(new Pending(op, null, Measure(op)));
    }

    public AetherAdmit TryAcceptContribution(IReadOnlyList<StoredField> fields, string principalId)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = fields.ToArray();
        foreach (var field in copy)
        {
            if (!BindsToPeer(field.WriterId, field.Version.ActorId, principalId)
                || !string.Equals(field.Key.WriterId, principalId, StringComparison.Ordinal))
                return AetherAdmit.Refused("Actor is not on this connection.");
        }

        return TryEnqueue(new Pending(null, null, Measure(copy), copy));
    }

    private static bool BindsToPeer(string writerId, string actorId, string principalId)
    {
        if (string.IsNullOrEmpty(principalId))
            return false;
        if (!string.Equals(writerId, principalId, StringComparison.Ordinal))
            return false;
        return AetherActorId.TryParse(actorId, out var actor)
            && string.Equals(actor.PeerId, principalId, StringComparison.Ordinal);
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

    public AetherAdmit TryAcceptLocalCreate(
        string entityId,
        string entityTypeId,
        string writerId,
        string actorId)
    {
        var bytes = BaseBytes + Utf8(entityId) + Utf8(entityTypeId) + Utf8(writerId) + Utf8(actorId) + 16;
        return TryEnqueue(new Pending(
            null,
            new LocalWrite(entityId, writerId, actorId, new Dictionary<string, FieldValue>(), entityTypeId),
            bytes));
    }

    public AetherAdmit TryAcceptLocalDelete(string entityId, string writerId, string actorId)
    {
        var bytes = BaseBytes + Utf8(entityId) + Utf8(writerId) + Utf8(actorId) + 16;
        return TryEnqueue(new Pending(
            null,
            new LocalWrite(
                entityId,
                writerId,
                actorId,
                new Dictionary<string, FieldValue>(),
                effect: AetherEffect.DeleteEntity),
            bytes));
    }

    public IReadOnlyList<AetherOutcome> Drain() => Drain(int.MaxValue, int.MaxValue, beforeFold: null);

    /// <summary>
    /// Folds until the inbox is empty or the next request would pass <paramref name="maxOperations"/>
    /// or <paramref name="maxBytes"/>. That request stays queued. The first request of the call is folded
    /// even when it alone is larger than <paramref name="maxBytes"/>, so one accepted request cannot stall.
    /// </summary>
    public IReadOnlyList<AetherOutcome> Drain(int maxOperations, int maxBytes) =>
        Drain(maxOperations, maxBytes, beforeFold: null);

    internal IReadOnlyList<AetherOutcome> Drain(Action<long>? beforeFold, Action? afterLocalStamp = null) =>
        Drain(int.MaxValue, int.MaxValue, beforeFold, afterLocalStamp);

    private IReadOnlyList<AetherOutcome> Drain(
        int maxOperations,
        int maxBytes,
        Action<long>? beforeFold,
        Action? afterLocalStamp = null)
    {
        if (maxOperations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOperations));
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var outcomes = new List<AetherOutcome>();
        var spentOperations = 0;
        long spentBytes = 0;
        while (_inbox.Count > 0)
        {
            var pending = _inbox.Peek();
            if (spentOperations >= maxOperations)
                break;
            if (spentOperations > 0 && spentBytes + pending.Bytes > maxBytes)
                break;

            FoldResult fold;
            AetherApplied folded = default;
            string? reason = null;
            try
            {
                beforeFold?.Invoke(pending.RequestId);
                fold = TryFold(pending, afterLocalStamp, out folded, out reason);
            }
            catch (Exception ex) when (ex is not AetherInterruptedException && outcomes.Count > 0)
            {
                throw new AetherInterruptedException(outcomes, ex);
            }

            if (fold == FoldResult.Rejected)
            {
                _inbox.Dequeue();
                _inboxBytes -= pending.Bytes;
                outcomes.Add(AetherOutcome.Rejected(pending.RequestId, reason!));
            }
            else if (fold == FoldResult.NoChange)
            {
                _inbox.Dequeue();
                _inboxBytes -= pending.Bytes;
                outcomes.Add(AetherOutcome.NoChange(pending.RequestId));
            }
            else
            {
                _inbox.Dequeue();
                _inboxBytes -= pending.Bytes;
                _outbox.Enqueue(pending.With(folded));
                _outboxBytes += pending.Bytes;
                outcomes.Add(AetherOutcome.Applied(pending.RequestId, folded));
            }

            spentOperations++;
            spentBytes += pending.Bytes;
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
        var bytes = BaseBytes
            + Utf8(op.Id)
            + Utf8(op.EntityId)
            + Utf8(op.WriterId)
            + Utf8(op.ActorId)
            + Utf8(op.PublicationId)
            + Utf8(op.EntityTypeId)
            + Utf8(op.CreatorId)
            + Utf8(op.OwnerId)
            + 16;
        foreach (var (fieldId, value) in op.Changes)
            bytes += Utf8(fieldId) + Utf8(value.Text) + 8;
        return bytes;
    }

    private static int Measure(IReadOnlyList<StoredField> fields)
    {
        var bytes = BaseBytes;
        foreach (var field in fields)
        {
            bytes += Utf8(field.Key.EntityId) + Utf8(field.Key.WriterId) + Utf8(field.Key.FieldId);
            bytes += Utf8(field.OperationId) + Utf8(field.Version.ActorId) + Utf8(field.Value.Text) + 24;
        }

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

    private FoldResult TryFold(Pending pending, Action? afterLocalStamp, out AetherApplied folded, out string? reason)
    {
        reason = null;
        if (pending.Contributions is not null)
        {
            try
            {
                var merged = Reducer.MergeContributions(pending.Contributions);
                if (!merged.Changed)
                {
                    folded = default;
                    return FoldResult.NoChange;
                }

                folded = merged.Applied;
            }
            catch (AetherProtocolException ex)
            {
                folded = default;
                reason = ex.Message;
                return FoldResult.Rejected;
            }

            return FoldResult.Applied;
        }

        if (pending.Remote is not null)
        {
            if (!AetherReducer.HasActorSequence(pending.Remote))
            {
                folded = default;
                reason = "Actor sequence is missing.";
                return FoldResult.Rejected;
            }

            try
            {
                folded = Reducer.Apply(pending.Remote, observeClock: true);
            }
            catch (AetherProtocolException ex)
            {
                folded = default;
                reason = ex.Message;
                return FoldResult.Rejected;
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
                    stamped = write.Effect switch
                    {
                        AetherEffect.CreateEntity => Reducer.StampLocalCreate(
                            write.EntityId,
                            write.EntityTypeId!,
                            write.WriterId,
                            write.ActorId),
                        AetherEffect.DeleteEntity => Reducer.StampLocalDelete(
                            write.EntityId,
                            write.WriterId,
                            write.ActorId),
                        _ => Reducer.StampLocal(write.EntityId, write.WriterId, write.ActorId, write.Changes),
                    };
                }
                catch (AetherProtocolException ex)
                {
                    folded = default;
                    reason = ex.Message;
                    return FoldResult.Rejected;
                }

                pending.Stamped = stamped;
                afterLocalStamp?.Invoke();
            }

            folded = Reducer.Apply(stamped, observeClock: false);
        }

        var operation = folded.Operation ?? throw new InvalidOperationException("A folded write has no operation.");
        Reducer.RestoreActorSequence(operation.ActorId, operation.Sequence);
        pending.Stamped = null;
        return FoldResult.Applied;
    }

    private enum FoldResult
    {
        Applied,
        Rejected,
        NoChange,
    }

    private static int Utf8(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private sealed class LocalWrite(
        string entityId,
        string writerId,
        string actorId,
        Dictionary<string, FieldValue> changes,
        string? entityTypeId = null,
        AetherEffect effect = AetherEffect.Write)
    {
        public string EntityId { get; } = entityId;
        public string WriterId { get; } = writerId;
        public string ActorId { get; } = actorId;
        public Dictionary<string, FieldValue> Changes { get; } = changes;
        public string? EntityTypeId { get; } = entityTypeId;
        public AetherEffect Effect { get; } = entityTypeId is null ? effect : AetherEffect.CreateEntity;
    }

    private sealed class Pending
    {
        public Pending(AetherOperation? remote, LocalWrite? local, int bytes, IReadOnlyList<StoredField>? contributions = null)
        {
            Remote = remote;
            Local = local;
            Contributions = contributions;
            Bytes = bytes;
        }

        public long RequestId { get; private set; }
        public AetherOperation? Remote { get; }
        public LocalWrite? Local { get; }
        public IReadOnlyList<StoredField>? Contributions { get; }
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
    Refused = 3,
}

/// <summary>
/// A request entered the queue, the queue was full, or the connection rejected the actor.
/// Full does not take a request id. Refused does not enter the queue.
/// </summary>
public readonly record struct AetherAdmit(AetherAdmitKind Kind, long RequestId, string? Reason = null)
{
    public static AetherAdmit Full => new(AetherAdmitKind.Full, 0);

    public static AetherAdmit Queued(long requestId) => new(AetherAdmitKind.Queued, requestId);

    public static AetherAdmit Refused(string reason) => new(AetherAdmitKind.Refused, 0, reason);
}

public enum AetherOutcomeKind : byte
{
    Applied = 1,
    Rejected = 2,
    NoChange = 3,
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

    public static AetherOutcome NoChange(long requestId) =>
        new(requestId, AetherOutcomeKind.NoChange, null, null);
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
