using System.Text;

namespace Ape.Core.Aether;

/// <summary>
/// Bounded input queue in front of one reducer. Local and remote writes drain through the same loop.
/// Saving and appending stay outside that loop.
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

    public bool TryAcceptRemote(AetherOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return TryEnqueue(new Pending(op, null, Measure(op)));
    }

    public bool TryAcceptLocal(
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

    public IReadOnlyList<AetherApplied> Drain()
    {
        var applied = new List<AetherApplied>(_inbox.Count);
        while (_inbox.Count > 0)
        {
            var pending = _inbox.Peek();
            if (!TryFold(pending, out var folded))
            {
                _inbox.Dequeue();
                _inboxBytes -= pending.Bytes;
                continue;
            }

            _inbox.Dequeue();
            _inboxBytes -= pending.Bytes;
            _outbox.Enqueue(pending.With(folded));
            _outboxBytes += pending.Bytes;
            applied.Add(folded);
        }

        return applied;
    }

    public void Commit(AetherArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        while (_outbox.Count > 0)
        {
            var pending = _outbox.Peek();
            archive.Append(pending.Applied!.Value);
            _outbox.Dequeue();
            _outboxBytes -= pending.Bytes;
        }
    }

    public void Save(AetherArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        archive.Save(Reducer);
        _outbox.Clear();
        _outboxBytes = 0;
    }

    public static int Measure(AetherOperation op)
    {
        var bytes = BaseBytes + Utf8(op.Id) + Utf8(op.EntityId) + Utf8(op.WriterId) + Utf8(op.ActorId) + Utf8(op.PublicationId) + 16;
        foreach (var (fieldId, value) in op.Changes)
            bytes += Utf8(fieldId) + Utf8(value.Text) + 8;
        return bytes;
    }

    private bool TryEnqueue(Pending pending)
    {
        if (_inbox.Count + _outbox.Count >= _maxCount)
            return false;
        if (pending.Bytes > _maxBytes || _inboxBytes + _outboxBytes + pending.Bytes > _maxBytes)
            return false;
        _inbox.Enqueue(pending);
        _inboxBytes += pending.Bytes;
        return true;
    }

    private bool TryFold(Pending pending, out AetherApplied folded)
    {
        if (pending.Remote is not null)
        {
            if (!AetherReducer.HasActorSequence(pending.Remote))
            {
                folded = default;
                return false;
            }

            try
            {
                folded = Reducer.Apply(pending.Remote, observeClock: true);
            }
            catch (AetherProtocolException)
            {
                folded = default;
                return false;
            }
        }
        else
        {
            var write = pending.Local!;
            if (string.IsNullOrEmpty(write.ActorId) || !Reducer.CanStamp(write.Changes))
            {
                folded = default;
                return false;
            }

            AetherOperation stamped;
            try
            {
                stamped = Reducer.StampLocal(write.EntityId, write.WriterId, write.ActorId, write.Changes);
            }
            catch (AetherProtocolException)
            {
                folded = default;
                return false;
            }

            folded = Reducer.Apply(stamped, observeClock: false);
        }

        Reducer.RestoreActorSequence(folded.Operation.ActorId, folded.Operation.Sequence);
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

        public AetherOperation? Remote { get; }
        public LocalWrite? Local { get; }
        public AetherApplied? Applied { get; private set; }
        public int Bytes { get; }

        public Pending With(AetherApplied applied)
        {
            Applied = applied;
            return this;
        }
    }
}
