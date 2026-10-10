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
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VisibilityState> _visibility = new(StringComparer.Ordinal);
    private readonly Dictionary<MembershipKey, MembershipState> _membership = new();
    private long _clock;
    private long _saveSequence;

    public AetherReducer(int dedupCapacity = DefaultDedupCapacity)
    {
        if (dedupCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(dedupCapacity));
        _dedupCapacity = dedupCapacity;
    }

    public long Clock => _clock;

    public int DedupCapacity => _dedupCapacity;

    public IReadOnlyList<FieldDefinition> Schema =>
        _fields
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new FieldDefinition(
                pair.Key,
                pair.Value.Kind == FieldKind.Lww ? "lww" : "sumContributions",
                pair.Value.Min,
                pair.Value.Max))
            .ToArray();

    public IReadOnlyList<ActorCursor> ActorCursors =>
        _nextSequence
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ActorCursor(pair.Key, pair.Value))
            .ToArray();

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

    public bool CanStamp(IReadOnlyDictionary<string, FieldValue> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var (fieldId, value) in changes)
        {
            if (!_fields.TryGetValue(fieldId, out var schema))
                return false;
            if (schema.Kind == FieldKind.Sum && value.IsText)
                return false;
            if (schema.Kind == FieldKind.Lww && !value.IsText)
                return false;
        }

        return true;
    }

    public static bool HasActorSequence(AetherOperation op) =>
        op is not null && !string.IsNullOrEmpty(op.ActorId) && op.Sequence >= 1;

    public AetherOperation StampLocal(
        string entityId,
        string writerId,
        string actorId,
        IReadOnlyDictionary<string, FieldValue> changes)
    {
        if (string.IsNullOrEmpty(actorId))
            throw new AetherProtocolException("Actor sequence is missing.");
        foreach (var (fieldId, value) in changes)
            RequireShape(fieldId, value);
        var nextClock = NextClock(_clock);
        var sequence = NextSequence(actorId);
        var stamped = new AetherOperation(
            $"{actorId}/{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            entityId,
            writerId,
            actorId,
            sequence,
            nextClock,
            changes);
        RejectBeforeAdmit(stamped);
        _clock = nextClock;
        _nextSequence[actorId] = sequence;
        return stamped;
    }

    /// <summary>
    /// Fold an operation. A newly admitted remote op sets the clock to max(clock, lamport) + 1,
    /// including when the remote Lamport is older. An exact duplicate still in the window leaves the clock alone.
    /// Evicting an id from the window does not reject any other operation.
    /// The returned clock is the clock after this call, paired with this operation.
    /// </summary>
    public AetherApplied Apply(AetherOperation op, bool observeClock)
    {
        ArgumentNullException.ThrowIfNull(op);
        RejectBeforeAdmit(op);
        if (_digests.ContainsKey(op.Id))
            return Applied(op);

        if (op.Effect != AetherEffect.Write)
        {
            ApplyLifecycle(op, observeClock);
            return Applied(op);
        }

        var version = new FieldVersion(op.Lamport, op.ActorId);
        Admit(op, observeClock);
        foreach (var (fieldId, value) in op.Changes)
            Upsert(new RecordKey(op.EntityId, op.WriterId, fieldId), value, version, op.WriterId, op.Id);
        return Applied(op);
    }

    /// <summary>
    /// Every protocol rejection Apply or StampLocal can raise. Nothing here writes the clock, the records, or the actor cursor.
    /// </summary>
    private void RejectBeforeAdmit(AetherOperation op)
    {
        if (_saveSequence == long.MaxValue)
            throw new AetherProtocolException("Save sequence overflow.");
        if (_digests.TryGetValue(op.Id, out var prior))
        {
            if (!string.Equals(prior, op.Digest, StringComparison.Ordinal))
                throw new AetherProtocolException($"Operation '{op.Id}' changed payload.");
            return;
        }

        if (op.Effect != AetherEffect.Write)
        {
            var version = new FieldVersion(op.Lamport, op.ActorId);
            switch (op.Effect)
            {
                case AetherEffect.DeleteEntity:
                    return;
                case AetherEffect.HideShared:
                    RejectVisibility(op.EntityId, version, hidden: true);
                    return;
                case AetherEffect.RestoreShared:
                    RejectVisibility(op.EntityId, version, hidden: false);
                    return;
                case AetherEffect.WithdrawPublication:
                    RejectMembership(op.EntityId, op.PublicationId!, version, published: false);
                    return;
                case AetherEffect.Publish:
                    RejectMembership(op.EntityId, op.PublicationId!, version, published: true);
                    return;
                default:
                    throw new AetherProtocolException($"Unknown effect '{op.Effect}'.");
            }
        }

        var writeVersion = new FieldVersion(op.Lamport, op.ActorId);
        foreach (var (fieldId, value) in op.Changes)
        {
            RequireShape(fieldId, value);
            var key = new RecordKey(op.EntityId, op.WriterId, fieldId);
            if (_records.TryGetValue(key, out var existing) && existing.Version.CompareTo(writeVersion) == 0 && existing.Value != value)
                throw new AetherProtocolException($"Version collision on '{fieldId}'.");
        }
    }

    /// <summary>How far the save sequence has moved. A snapshot covers every application up to this place.</summary>
    public long SaveThrough => _saveSequence;

    internal void RestoreSaveThrough(long saveSequence)
    {
        if (saveSequence < 0 || saveSequence < _saveSequence)
            throw new AetherProtocolException("Save sequence moved backwards.");
        _saveSequence = saveSequence;
    }

    private AetherApplied Applied(AetherOperation op)
    {
        _saveSequence++;
        return new AetherApplied(op, _clock, _saveSequence);
    }

    public void MergeImage(AetherImage image)
    {
        var folded = FoldImage(image);

        if (image.Clock > _clock)
            _clock = image.Clock;

        foreach (var seen in image.Seen)
            Remember(seen.Id, seen.Digest, seen.Lamport);

        foreach (var field in folded.Fields)
            Upsert(field.Key, field.Value, field.Version, field.WriterId, field.OperationId);
        foreach (var entityId in folded.Deleted)
            _deleted.Add(entityId);
        foreach (var mark in folded.Visibility)
            UpsertVisibility(mark.EntityId, mark.Version, mark.Hidden);
        foreach (var mark in folded.Membership)
            UpsertMembership(new MembershipKey(mark.EntityId, mark.PublicationId), mark.Version, mark.Published);
    }

    public AetherImage Capture()
    {
        var fields = _records.Values
            .OrderBy(f => f.Key.EntityId, StringComparer.Ordinal)
            .ThenBy(f => f.Key.WriterId, StringComparer.Ordinal)
            .ThenBy(f => f.Key.FieldId, StringComparer.Ordinal)
            .ToArray();
        var deleted = _deleted.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var visibility = _visibility
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new VisibilityMark(pair.Key, pair.Value.Version, pair.Value.Hidden))
            .ToArray();
        var membership = _membership
            .OrderBy(pair => pair.Key.EntityId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.PublicationId, StringComparer.Ordinal)
            .Select(pair => new MembershipMark(pair.Key.EntityId, pair.Key.PublicationId, pair.Value.Version, pair.Value.Published))
            .ToArray();
        return new AetherImage(_clock, fields, _seenOrder.ToArray(), deleted, visibility, membership);
    }

    public void RestoreActorSequence(string actorId, long sequence)
    {
        if (string.IsNullOrEmpty(actorId) || sequence < 1)
            throw new AetherProtocolException("Actor sequence is missing.");
        if (_nextSequence.TryGetValue(actorId, out var current) && current >= sequence)
            return;
        _nextSequence[actorId] = sequence;
    }

    internal void RestoreClock(long clock) => _clock = clock;

    public bool IsDeleted(string entityId) => _deleted.Contains(entityId);

    public bool IsSharedVisible(string entityId) =>
        !_visibility.TryGetValue(entityId, out var state) || !state.Hidden;

    public bool IsMember(string entityId, string publicationId) =>
        _membership.TryGetValue(new MembershipKey(entityId, publicationId), out var state) && state.Published;

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

    private void ApplyLifecycle(AetherOperation op, bool observeClock)
    {
        var version = new FieldVersion(op.Lamport, op.ActorId);
        switch (op.Effect)
        {
            case AetherEffect.DeleteEntity:
                Admit(op, observeClock);
                _deleted.Add(op.EntityId);
                return;
            case AetherEffect.HideShared:
                RejectVisibility(op.EntityId, version, hidden: true);
                Admit(op, observeClock);
                UpsertVisibility(op.EntityId, version, hidden: true);
                return;
            case AetherEffect.RestoreShared:
                RejectVisibility(op.EntityId, version, hidden: false);
                Admit(op, observeClock);
                UpsertVisibility(op.EntityId, version, hidden: false);
                return;
            case AetherEffect.WithdrawPublication:
                RejectMembership(op.EntityId, op.PublicationId!, version, published: false);
                Admit(op, observeClock);
                UpsertMembership(new MembershipKey(op.EntityId, op.PublicationId!), version, published: false);
                return;
            case AetherEffect.Publish:
                RejectMembership(op.EntityId, op.PublicationId!, version, published: true);
                Admit(op, observeClock);
                UpsertMembership(new MembershipKey(op.EntityId, op.PublicationId!), version, published: true);
                return;
            default:
                throw new AetherProtocolException($"Unknown effect '{op.Effect}'.");
        }
    }

    private void Admit(AetherOperation op, bool observeClock)
    {
        if (observeClock)
            _clock = NextClock(Math.Max(_clock, op.Lamport));
        Remember(op.Id, op.Digest, op.Lamport);
    }

    private FoldedImage FoldImage(AetherImage image)
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

        return new FoldedImage(folded, FoldDeleted(image.Deleted), FoldVisibility(image.Visibility), FoldMembership(image.Membership));
    }

    private static List<string> FoldDeleted(IReadOnlyList<string>? deleted)
    {
        var ids = new List<string>();
        if (deleted is null)
            return ids;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityId in deleted)
        {
            if (seen.Add(entityId))
                ids.Add(entityId);
        }

        return ids;
    }

    private List<VisibilityMark> FoldVisibility(IReadOnlyList<VisibilityMark>? rows)
    {
        var versions = new Dictionary<string, Dictionary<FieldVersion, VisibilityMark>>(StringComparer.Ordinal);
        foreach (var row in rows ?? [])
        {
            if (_visibility.TryGetValue(row.EntityId, out var existing)
                && existing.Version.CompareTo(row.Version) == 0
                && existing.Hidden != row.Hidden)
                throw new AetherProtocolException($"Version collision on visibility for '{row.EntityId}'.");

            if (!versions.TryGetValue(row.EntityId, out var byVersion))
            {
                byVersion = new Dictionary<FieldVersion, VisibilityMark>();
                versions[row.EntityId] = byVersion;
            }

            if (byVersion.TryGetValue(row.Version, out var already) && already.Hidden != row.Hidden)
                throw new AetherProtocolException($"Version collision on visibility for '{row.EntityId}'.");

            byVersion[row.Version] = row;
        }

        var folded = new List<VisibilityMark>(versions.Count);
        foreach (var byVersion in versions.Values)
        {
            VisibilityMark? winner = null;
            foreach (var row in byVersion.Values)
            {
                if (winner is null || row.Version > winner.Value.Version)
                    winner = row;
            }

            folded.Add(winner!.Value);
        }

        return folded;
    }

    private List<MembershipMark> FoldMembership(IReadOnlyList<MembershipMark>? rows)
    {
        var versions = new Dictionary<MembershipKey, Dictionary<FieldVersion, MembershipMark>>();
        foreach (var row in rows ?? [])
        {
            var key = new MembershipKey(row.EntityId, row.PublicationId);
            if (_membership.TryGetValue(key, out var existing)
                && existing.Version.CompareTo(row.Version) == 0
                && existing.Published != row.Published)
                throw new AetherProtocolException($"Version collision on membership '{row.PublicationId}'.");

            if (!versions.TryGetValue(key, out var byVersion))
            {
                byVersion = new Dictionary<FieldVersion, MembershipMark>();
                versions[key] = byVersion;
            }

            if (byVersion.TryGetValue(row.Version, out var already) && already.Published != row.Published)
                throw new AetherProtocolException($"Version collision on membership '{row.PublicationId}'.");

            byVersion[row.Version] = row;
        }

        var folded = new List<MembershipMark>(versions.Count);
        foreach (var byVersion in versions.Values)
        {
            MembershipMark? winner = null;
            foreach (var row in byVersion.Values)
            {
                if (winner is null || row.Version > winner.Value.Version)
                    winner = row;
            }

            folded.Add(winner!.Value);
        }

        return folded;
    }

    private void RejectVisibility(string entityId, FieldVersion version, bool hidden)
    {
        if (_visibility.TryGetValue(entityId, out var existing)
            && existing.Version.CompareTo(version) == 0
            && existing.Hidden != hidden)
            throw new AetherProtocolException($"Version collision on visibility for '{entityId}'.");
    }

    private void RejectMembership(string entityId, string publicationId, FieldVersion version, bool published)
    {
        if (_membership.TryGetValue(new MembershipKey(entityId, publicationId), out var existing)
            && existing.Version.CompareTo(version) == 0
            && existing.Published != published)
            throw new AetherProtocolException($"Version collision on membership '{publicationId}'.");
    }

    private void UpsertVisibility(string entityId, FieldVersion version, bool hidden)
    {
        if (_visibility.TryGetValue(entityId, out var existing))
        {
            var compared = version.CompareTo(existing.Version);
            if (compared < 0)
                return;
            if (compared == 0)
            {
                if (existing.Hidden != hidden)
                    throw new InvalidOperationException($"Version collision on visibility for '{entityId}'.");
                return;
            }
        }

        _visibility[entityId] = new VisibilityState(version, hidden);
    }

    private void UpsertMembership(MembershipKey key, FieldVersion version, bool published)
    {
        if (_membership.TryGetValue(key, out var existing))
        {
            var compared = version.CompareTo(existing.Version);
            if (compared < 0)
                return;
            if (compared == 0)
            {
                if (existing.Published != published)
                    throw new InvalidOperationException($"Version collision on membership '{key.PublicationId}'.");
                return;
            }
        }

        _membership[key] = new MembershipState(version, published);
    }

    private static long NextClock(long clock)
    {
        if (clock == long.MaxValue)
            throw new AetherProtocolException("Lamport clock overflow.");
        return clock + 1;
    }

    private long NextSequence(string actorId)
    {
        if (!_nextSequence.TryGetValue(actorId, out var sequence))
            return 1;
        if (sequence == long.MaxValue)
            throw new AetherProtocolException("Actor sequence overflow.");
        return sequence + 1;
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
                    throw new InvalidOperationException($"Version collision on '{key.FieldId}'.");
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

    private readonly record struct VisibilityState(FieldVersion Version, bool Hidden);

    private readonly record struct MembershipState(FieldVersion Version, bool Published);

    private readonly record struct FoldedImage(
        List<StoredField> Fields,
        List<string> Deleted,
        List<VisibilityMark> Visibility,
        List<MembershipMark> Membership);
}

public readonly record struct FieldDefinition(string Id, string Resolver, long Min, long Max);

public readonly record struct ActorCursor(string ActorId, long Sequence);

public readonly record struct RecordKey(string EntityId, string WriterId, string FieldId);

public sealed record StoredField(
    RecordKey Key,
    FieldValue Value,
    FieldVersion Version,
    string WriterId,
    string OperationId);

public readonly record struct SeenOperation(string Id, string Digest, long Lamport);

/// <summary>
/// One application and the clock after that application. A later write does not change this pair.
/// </summary>
public readonly record struct AetherApplied(AetherOperation Operation, long Clock, long SaveSequence);

public readonly record struct MembershipKey(string EntityId, string PublicationId);

public readonly record struct VisibilityMark(string EntityId, FieldVersion Version, bool Hidden);

public readonly record struct MembershipMark(string EntityId, string PublicationId, FieldVersion Version, bool Published);

public sealed record AetherImage(
    long Clock,
    IReadOnlyList<StoredField> Fields,
    IReadOnlyList<SeenOperation> Seen,
    IReadOnlyList<string>? Deleted = null,
    IReadOnlyList<VisibilityMark>? Visibility = null,
    IReadOnlyList<MembershipMark>? Membership = null);
