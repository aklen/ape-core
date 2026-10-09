namespace Ape.Core.Aether;

/// <summary>
/// Folded contribution store. LWW keeps one record per writer and field.
/// A sum keeps one raw contribution per writer. The saturated total is a view.
/// </summary>
public sealed class AetherReducer
{
    public const int DefaultDedupCapacity = 1024;

    private readonly int _dedupCapacity;
    private readonly Dictionary<string, FieldSchema> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<RecordKey, StoredField> _records = new();
    private readonly Dictionary<string, string> _digests = new(StringComparer.Ordinal);
    private readonly Queue<SeenOperation> _seenOrder = new();
    private readonly Dictionary<string, long> _nextSequence = new(StringComparer.Ordinal);
    private long _clock;

    public AetherReducer(int dedupCapacity = DefaultDedupCapacity)
    {
        if (dedupCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(dedupCapacity));
        _dedupCapacity = dedupCapacity;
    }

    public long Clock => _clock;

    public int RecordCount => _records.Count;

    public int DedupCount => _digests.Count;

    public bool Remembers(string operationId) => _digests.ContainsKey(operationId);

    /// <summary>
    /// True only when the original operation payload is still stored.
    /// The dedup window keeps digests, so this slice cannot replay a delta.
    /// </summary>
    public bool CanReplay(string operationId) => false;

    public void DefineField(string fieldId, string resolver, long min = long.MinValue, long max = long.MaxValue)
    {
        if (resolver is not ("lww" or "sumContributions"))
            throw new AetherProtocolException($"Unknown resolver '{resolver}'.");
        if (min > max)
            throw new AetherProtocolException($"Field '{fieldId}' has a minimum above its maximum.");
        if (_fields.ContainsKey(fieldId))
            throw new AetherProtocolException($"Field '{fieldId}' is already defined.");

        _fields[fieldId] = new FieldSchema(
            resolver == "lww" ? FieldKind.Lww : FieldKind.Sum,
            min,
            max);
    }

    public AetherOperation StampLocal(
        string entityId,
        string writerId,
        string actorId,
        IReadOnlyDictionary<string, FieldValue> changes)
    {
        var nextClock = NextClock(_clock);
        _clock = nextClock;
        if (!_nextSequence.TryGetValue(actorId, out var sequence))
            sequence = 1;
        else
            sequence++;
        _nextSequence[actorId] = sequence;

        return new AetherOperation(
            $"{actorId}/{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            entityId,
            writerId,
            actorId,
            sequence,
            _clock,
            changes);
    }

    /// <summary>
    /// Fold an operation. A newly admitted remote op sets the clock to max(clock, lamport) + 1,
    /// including when the remote Lamport is older. An exact duplicate still in the window leaves the clock alone.
    /// Evicting an id from the window does not reject any other operation.
    /// </summary>
    public void Apply(AetherOperation op, bool observeClock)
    {
        if (_digests.TryGetValue(op.Id, out var prior))
        {
            if (!string.Equals(prior, op.Digest, StringComparison.Ordinal))
                throw new AetherProtocolException($"Operation '{op.Id}' changed payload.");
            return;
        }

        var version = new FieldVersion(op.Lamport, op.ActorId);
        var admitted = new List<(RecordKey Key, FieldValue Value)>(op.Changes.Count);
        foreach (var (fieldId, value) in op.Changes)
        {
            RequireShape(fieldId, value);

            var key = new RecordKey(op.EntityId, op.WriterId, fieldId);
            if (_records.TryGetValue(key, out var existing) && existing.Version.CompareTo(version) == 0 && existing.Value != value)
                throw new AetherProtocolException($"Version collision on '{fieldId}'.");

            admitted.Add((key, value));
        }

        if (observeClock)
            _clock = NextClock(Math.Max(_clock, op.Lamport));

        Remember(op.Id, op.Digest, op.Lamport);
        foreach (var (key, value) in admitted)
            Upsert(key, value, version, op.WriterId, op.Id);
    }

    public void MergeImage(AetherImage image)
    {
        var folded = FoldImage(image);

        if (image.Clock > _clock)
            _clock = image.Clock;

        foreach (var seen in image.Seen)
            Remember(seen.Id, seen.Digest, seen.Lamport);

        foreach (var field in folded)
            Upsert(field.Key, field.Value, field.Version, field.WriterId, field.OperationId);
    }

    public AetherImage Capture()
    {
        var fields = _records.Values
            .OrderBy(f => f.Key.EntityId, StringComparer.Ordinal)
            .ThenBy(f => f.Key.WriterId, StringComparer.Ordinal)
            .ThenBy(f => f.Key.FieldId, StringComparer.Ordinal)
            .ToArray();
        return new AetherImage(_clock, fields, _seenOrder.ToArray());
    }

    public FieldValue? ResolveLww(string entityId, string fieldId)
    {
        StoredField? winner = null;
        foreach (var field in _records.Values)
        {
            if (!string.Equals(field.Key.EntityId, entityId, StringComparison.Ordinal)
                || !string.Equals(field.Key.FieldId, fieldId, StringComparison.Ordinal))
                continue;

            if (winner is null || field.Version > winner.Version)
                winner = field;
        }

        return winner?.Value;
    }

    public long ResolveSum(string entityId, string fieldId)
    {
        if (!_fields.TryGetValue(fieldId, out var schema) || schema.Kind != FieldKind.Sum)
            throw new AetherProtocolException($"Field '{fieldId}' is not a sum.");

        Int128 total = 0;
        foreach (var field in _records.Values
                     .Where(f => string.Equals(f.Key.EntityId, entityId, StringComparison.Ordinal)
                                 && string.Equals(f.Key.FieldId, fieldId, StringComparison.Ordinal))
                     .OrderBy(f => f.Key.WriterId, StringComparer.Ordinal))
        {
            total += field.Value.Fixed;
        }

        if (total > schema.Max)
            return schema.Max;
        if (total < schema.Min)
            return schema.Min;
        return (long)total;
    }

    public long RawContribution(string entityId, string writerId, string fieldId)
    {
        if (!_records.TryGetValue(new RecordKey(entityId, writerId, fieldId), out var field))
            throw new AetherProtocolException("Missing contribution.");
        return field.Value.Fixed;
    }

    private List<StoredField> FoldImage(AetherImage image)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seen in image.Seen)
        {
            if (digests.TryGetValue(seen.Id, out var prior) && !string.Equals(prior, seen.Digest, StringComparison.Ordinal))
                throw new AetherProtocolException($"Operation '{seen.Id}' changed payload.");
            if (_digests.TryGetValue(seen.Id, out var local) && !string.Equals(local, seen.Digest, StringComparison.Ordinal))
                throw new AetherProtocolException($"Operation '{seen.Id}' changed payload.");

            digests[seen.Id] = seen.Digest;
        }

        var versions = new Dictionary<RecordKey, Dictionary<FieldVersion, StoredField>>();
        foreach (var field in image.Fields)
        {
            RequireShape(field.Key.FieldId, field.Value);
            if (_records.TryGetValue(field.Key, out var existing)
                && existing.Version.CompareTo(field.Version) == 0
                && existing.Value != field.Value)
                throw new AetherProtocolException($"Version collision on '{field.Key.FieldId}'.");

            if (!versions.TryGetValue(field.Key, out var byVersion))
            {
                byVersion = new Dictionary<FieldVersion, StoredField>();
                versions[field.Key] = byVersion;
            }

            if (byVersion.TryGetValue(field.Version, out var already) && already.Value != field.Value)
                throw new AetherProtocolException($"Version collision on '{field.Key.FieldId}'.");

            byVersion[field.Version] = field;
        }

        var folded = new List<StoredField>(versions.Count);
        foreach (var byVersion in versions.Values)
        {
            StoredField? winner = null;
            foreach (var field in byVersion.Values)
            {
                if (winner is null || field.Version > winner.Version)
                    winner = field;
            }

            folded.Add(winner!);
        }

        return folded;
    }

    private static long NextClock(long clock)
    {
        if (clock == long.MaxValue)
            throw new AetherProtocolException("Lamport clock overflow.");
        return clock + 1;
    }

    private void RequireShape(string fieldId, FieldValue value)
    {
        if (!_fields.TryGetValue(fieldId, out var schema))
            throw new AetherProtocolException($"Field '{fieldId}' has no resolver.");

        if (schema.Kind == FieldKind.Sum && value.IsText)
            throw new AetherProtocolException($"Field '{fieldId}' stores a fixed-point contribution.");
        if (schema.Kind == FieldKind.Lww && !value.IsText)
            throw new AetherProtocolException($"Field '{fieldId}' stores a label.");
    }

    private void Upsert(RecordKey key, FieldValue value, FieldVersion version, string writerId, string operationId)
    {
        if (_records.TryGetValue(key, out var existing))
        {
            var compared = version.CompareTo(existing.Version);
            if (compared < 0)
                return;
            if (compared == 0)
            {
                if (existing.Value != value)
                    throw new AetherProtocolException($"Version collision on '{key.FieldId}'.");
                return;
            }
        }

        _records[key] = new StoredField(key, value, version, writerId, operationId);
    }

    private void Remember(string id, string digest, long lamport)
    {
        if (_digests.ContainsKey(id))
            return;

        _digests[id] = digest;
        _seenOrder.Enqueue(new SeenOperation(id, digest, lamport));
        while (_seenOrder.Count > _dedupCapacity)
        {
            var evicted = _seenOrder.Dequeue();
            _digests.Remove(evicted.Id);
        }
    }

    private enum FieldKind
    {
        Lww,
        Sum,
    }

    private readonly record struct FieldSchema(FieldKind Kind, long Min, long Max);
}

public readonly record struct RecordKey(string EntityId, string WriterId, string FieldId);

public sealed record StoredField(
    RecordKey Key,
    FieldValue Value,
    FieldVersion Version,
    string WriterId,
    string OperationId);

public readonly record struct SeenOperation(string Id, string Digest, long Lamport);

public sealed record AetherImage(
    long Clock,
    IReadOnlyList<StoredField> Fields,
    IReadOnlyList<SeenOperation> Seen);
