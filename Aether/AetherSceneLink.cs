using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;

namespace Ape.Core.Aether;

/// <summary>
/// Several entities of one registered type on one authenticated link.
/// Each entity carries a label and one sum. The frame stays three stages.
/// </summary>
public sealed class AetherSceneLink : IDisposable
{
    private readonly INetworkManager _network;
    private readonly IPrincipalSigner? _principalSigner;
    private readonly AetherHost _host;
    private readonly ISceneManager _scene;
    private readonly ILogger _logger;
    private readonly string _writerId;
    private readonly string _actorId;
    private readonly string _publicationId;
    private readonly string _labelFieldId;
    private readonly string _sumFieldId;
    private readonly string _labelProperty;
    private readonly string _sumProperty;
    private readonly int _maxOutgoing;
    private readonly Func<AetherOperation, AetherOperation>? _authoritySeal;
    private readonly Dictionary<string, AetherOperation> _ownVisibility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AetherOperation> _ownMembership = new(StringComparer.Ordinal);
    private readonly HashSet<long> _localIds = new();
    private readonly Queue<byte[]> _outgoing = new();
    private readonly Queue<HeldOperation> _held = new();
    private readonly Queue<HeldContribution> _heldContributions = new();
    private readonly FrozenPlan<AetherScratch> _plan;
    private PlanRuntime _runtime;
    private AetherScratch _scratch;
    private bool _catchUpPending;
    private bool _stopped;

    public AetherSceneLink(
        INetworkManager network,
        AetherHost host,
        ISceneManager scene,
        ILogger logger,
        string writerId,
        string sessionId,
        string publicationId = "catalog",
        string labelFieldId = "label",
        string sumFieldId = "offset",
        string labelPropertyName = "Label",
        string sumPropertyName = "Offset",
        int maxOutgoing = 64)
    {
        if (maxOutgoing < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOutgoing));
        _maxOutgoing = maxOutgoing;
        _principalSigner = network as IPrincipalSigner;
        _authoritySeal = _principalSigner?.HasPrincipalSigner == true ? SealAuthority : null;
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writerId = writerId ?? throw new ArgumentNullException(nameof(writerId));
        _publicationId = publicationId ?? throw new ArgumentNullException(nameof(publicationId));
        _actorId = AetherActorId.Format(writerId, sessionId);
        _labelFieldId = labelFieldId ?? throw new ArgumentNullException(nameof(labelFieldId));
        _sumFieldId = sumFieldId ?? throw new ArgumentNullException(nameof(sumFieldId));
        _labelProperty = labelPropertyName ?? throw new ArgumentNullException(nameof(labelPropertyName));
        _sumProperty = sumPropertyName ?? throw new ArgumentNullException(nameof(sumPropertyName));
        _plan = AetherFrame.Compile();
        _runtime = new PlanRuntime();
        _plan.InitRuntime(ref _runtime);
        _scratch = new AetherScratch
        {
            Host = host,
            Reducer = host.Reducer,
            FrameOperationBudget = 64,
            FrameByteBudget = 1_000_000,
            LabelFieldId = labelFieldId,
            SumFieldId = sumFieldId,
            PublicationId = publicationId,
            ProjectScene = true,
            LabelPropertyName = labelPropertyName,
            SumPropertyName = sumPropertyName,
        };
        network.OnDataReceived += OnData;
        network.OnPeerConnected += OnPeerConnected;
    }

    public AetherAdmit? LastRemoteAdmit { get; private set; }

    public string? LastRemoteFault { get; private set; }

    public AetherAdmit SubmitCreate(string entityId, string entityTypeId)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocalCreate(entityId, entityTypeId, _writerId, _actorId, _authoritySeal);
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    public AetherAdmit SubmitLabel(string entityId, string label)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        return SubmitField(entityId, _labelFieldId, FieldValue.Label(label));
    }

    public AetherAdmit SubmitPublish(string entityId) =>
        SubmitLifecycle(entityId, publish: true);

    public AetherAdmit SubmitWithdraw(string entityId) =>
        SubmitLifecycle(entityId, publish: false);

    public AetherAdmit SubmitHide(string entityId) =>
        SubmitVisibility(entityId, hidden: true);

    public AetherAdmit SubmitRestore(string entityId) =>
        SubmitVisibility(entityId, hidden: false);

    public AetherAdmit SubmitOffset(string entityId, long offset)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        return SubmitField(entityId, _sumFieldId, FieldValue.FixedPoint(offset));
    }

    public void CatchUp()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        _catchUpPending = true;
    }

    public void Pump()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        AdmitHeld();
        AdmitContributions();
        _plan.Tick(ref _scratch, ref _runtime);
        AdmitHeld();
        NoteAetherEntities();
        if (_scratch.SceneCommits is not null)
        {
            foreach (var request in _scratch.SceneCommits)
                SceneCommitApplicator.Apply(_scene, request, _logger);
        }

        YieldProjected();
        if (_scratch.Outcomes is not null)
        {
            foreach (var outcome in _scratch.Outcomes)
            {
                if (!_localIds.Contains(outcome.RequestId))
                    continue;
                _localIds.Remove(outcome.RequestId);
                if (outcome.Kind != AetherOutcomeKind.Applied || outcome.Result is not { Operation: { } operation })
                    continue;
                Note(operation);
                if (_outgoing.Count >= _maxOutgoing)
                    continue;
                _outgoing.Enqueue(EncodeLive(operation));
            }
        }

        Flush();
        ScheduleCatchUp();
        Flush();
    }

    public void Dispose()
    {
        if (_stopped)
            return;
        _stopped = true;
        _network.OnPeerConnected -= OnPeerConnected;
        _network.OnDataReceived -= OnData;
    }

    private AetherAdmit SubmitLifecycle(string entityId, bool publish)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocalPublish(entityId, _publicationId, _writerId, _actorId, publish, _authoritySeal);
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    private AetherAdmit SubmitVisibility(string entityId, bool hidden)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocalHide(entityId, _writerId, _actorId, hidden, _authoritySeal);
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    private AetherAdmit SubmitField(string entityId, string fieldId, FieldValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocal(entityId, _writerId, _actorId, new Dictionary<string, FieldValue>
        {
            [fieldId] = value,
        });
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    private void OnData(string connectionId, byte[] data)
    {
        if (_stopped || !AetherWire.IsStatePacket(data))
            return;
        if (data.Length > AetherWire.MaxPacketBytes)
        {
            LastRemoteFault = AetherWire.PacketTooLarge;
            LastRemoteAdmit = null;
            return;
        }

        if (!TryReadRemote(data, out var operation, out var fields))
            return;
        if (!_network.TryGetPeer(connectionId, out var peer) || !peer.IsAuthenticated)
        {
            LastRemoteFault = "Peer is not authenticated.";
            LastRemoteAdmit = null;
            return;
        }

        AcceptRemote(peer.PrincipalId, operation, fields);
    }

    private void OnPeerConnected(string peerId) => CatchUp();

    private bool TryReadRemote(byte[] data, out AetherOperation? operation, out IReadOnlyList<StoredField>? fields)
    {
        operation = null;
        fields = null;
        if (AetherWire.IsContributionPacket(data))
        {
            if (!AetherWire.TryDecodeContributions(data, out var decoded, out var fault))
            {
                LastRemoteFault = fault;
                LastRemoteAdmit = null;
                return false;
            }

            foreach (var field in decoded)
            {
                if (!IsSceneField(field.Key.FieldId))
                {
                    LastRemoteFault = "Field is not on this link.";
                    LastRemoteAdmit = null;
                    return false;
                }
            }

            fields = decoded;
            return true;
        }

        if (!AetherWire.TryDecodeAny(data, out operation, out var operationFault))
        {
            LastRemoteFault = operationFault;
            LastRemoteAdmit = null;
            return false;
        }

        if (operation!.Effect == AetherEffect.Write)
        {
            if (operation.Changes.Count != 1 || !IsSceneField(operation.Changes.Keys.Single()))
            {
                LastRemoteFault = "Field is not on this link.";
                LastRemoteAdmit = null;
                operation = null;
                return false;
            }
        }

        return true;
    }

    private bool IsSceneField(string fieldId) =>
        string.Equals(fieldId, _labelFieldId, StringComparison.Ordinal)
        || string.Equals(fieldId, _sumFieldId, StringComparison.Ordinal);

    private void AcceptRemote(string principalId, AetherOperation? operation, IReadOnlyList<StoredField>? fields)
    {
        if (fields is not null)
        {
            QueueContribution(principalId, fields);
            return;
        }

        LastRemoteFault = null;
        var admit = _host.TryAcceptRemote(operation!, principalId, requireAuthorityProof: true);
        LastRemoteAdmit = admit;
        if (admit.Kind == AetherAdmitKind.Refused)
        {
            LastRemoteFault = admit.Reason;
            if (IsMissingDependency(admit.Reason))
                HoldOperation(principalId, operation!);
            return;
        }

        if (admit.Kind == AetherAdmitKind.Queued)
            return;

        HoldOperation(principalId, operation!);
    }

    private void HoldOperation(string principalId, AetherOperation operation)
    {
        if (_held.Any(held => held.Operation.Id == operation.Id))
            return;
        if (_held.Count >= _maxOutgoing)
        {
            LastRemoteFault = "Catch-up does not fit.";
            return;
        }

        _held.Enqueue(new HeldOperation(principalId, operation));
    }

    private void AdmitHeld()
    {
        var waiting = _held.Count;
        for (var i = 0; i < waiting; i++)
        {
            var held = _held.Dequeue();
            var admit = _host.TryAcceptRemote(held.Operation, held.PrincipalId, requireAuthorityProof: true);
            LastRemoteAdmit = admit;
            if (admit.Kind == AetherAdmitKind.Refused)
            {
                LastRemoteFault = admit.Reason;
                if (IsMissingDependency(admit.Reason))
                    _held.Enqueue(held);
                continue;
            }

            if (admit.Kind == AetherAdmitKind.Queued)
            {
                LastRemoteFault = null;
                continue;
            }

            _held.Enqueue(held);
        }
    }

    private void QueueContribution(string principalId, IReadOnlyList<StoredField> fields)
    {
        if (_heldContributions.Count >= _maxOutgoing)
        {
            LastRemoteFault = "Catch-up does not fit.";
            return;
        }

        LastRemoteFault = null;
        _heldContributions.Enqueue(new HeldContribution(principalId, fields));
    }

    private void AdmitContributions()
    {
        var waiting = _heldContributions.Count;
        for (var i = 0; i < waiting; i++)
        {
            var held = _heldContributions.Dequeue();
            var admit = _host.TryAcceptContribution(held.Fields, held.PrincipalId);
            LastRemoteAdmit = admit;
            if (admit.Kind == AetherAdmitKind.Refused)
            {
                LastRemoteFault = admit.Reason;
                continue;
            }

            if (admit.Kind == AetherAdmitKind.Queued)
                continue;

            _heldContributions.Enqueue(held);
        }
    }

    private void ScheduleCatchUp()
    {
        if (!_catchUpPending)
            return;
        var packets = BuildCatchUp();
        if (_outgoing.Count + packets.Count > _maxOutgoing)
            return;
        foreach (var packet in packets)
            _outgoing.Enqueue(packet);
        _catchUpPending = false;
    }

    private List<byte[]> BuildCatchUp()
    {
        var packets = new List<byte[]>();
        foreach (var entity in _host.Reducer.Entities)
        {
            if (string.Equals(entity.CreatorId, _writerId, StringComparison.Ordinal))
                packets.Add(AetherWire.Encode(CreationOperation(entity)));
        }

        foreach (var row in _host.Reducer.Ownership)
        {
            if (string.Equals(row.PreviousOwnerId, _writerId, StringComparison.Ordinal))
                packets.Add(AetherWire.Encode(OwnershipOperation(row)));
        }

        foreach (var deletion in _host.Reducer.Deletions)
        {
            if (string.Equals(deletion.WriterId, _writerId, StringComparison.Ordinal))
                packets.Add(AetherWire.Encode(DeletionOperation(deletion)));
        }

        foreach (var operation in _ownVisibility.Values.OrderBy(operation => operation.EntityId, StringComparer.Ordinal))
            packets.Add(AetherWire.Encode(operation));
        foreach (var operation in _ownMembership.Values.OrderBy(operation => operation.EntityId, StringComparer.Ordinal))
            packets.Add(AetherWire.Encode(operation));

        var fields = OwnFields();
        foreach (var entityId in fields
                     .Select(field => field.Key.EntityId)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var group = fields
                .Where(field => string.Equals(field.Key.EntityId, entityId, StringComparison.Ordinal))
                .ToArray();
            packets.Add(AetherWire.EncodeContributions(group));
        }

        return packets;
    }

    private List<StoredField> OwnFields()
    {
        var records = new List<StoredField>();
        foreach (var field in _host.Reducer.Capture().Fields)
        {
            if (!string.Equals(field.Key.WriterId, _writerId, StringComparison.Ordinal) || !IsSceneField(field.Key.FieldId))
                continue;
            records.Add(field);
        }

        return records;
    }

    private void Flush()
    {
        while (_outgoing.Count > 0)
        {
            var packet = _outgoing.Dequeue();
            _network.Broadcast(packet, reliable: true);
        }
    }

    private void NoteAetherEntities()
    {
        if (_scene is not SceneManager manager)
            return;
        foreach (var entity in _host.Reducer.Entities)
            manager.NoteAetherEntity(entity.EntityId);
    }

    private void YieldProjected()
    {
        foreach (var entity in _host.Reducer.Entities)
        {
            if (_scene.GetEntity(entity.EntityId) is not Replica replica)
                continue;
            replica.YieldPropertyToAether(_labelProperty);
            replica.YieldPropertyToAether(_sumProperty);
        }
    }

    private void Note(AetherOperation operation)
    {
        if (!string.Equals(operation.WriterId, _writerId, StringComparison.Ordinal))
            return;
        if (operation.Effect is AetherEffect.HideShared or AetherEffect.RestoreShared)
            _ownVisibility[operation.EntityId] = operation;
        if (operation.Effect is AetherEffect.Publish or AetherEffect.WithdrawPublication
            && string.Equals(operation.PublicationId, _publicationId, StringComparison.Ordinal))
            _ownMembership[operation.EntityId] = operation;
    }

    private static byte[] EncodeLive(AetherOperation operation)
    {
        if (operation.Effect != AetherEffect.Write || operation.Changes.Count != 1)
            return AetherWire.Encode(operation);
        var change = operation.Changes.Single();
        if (change.Value.IsText)
            return AetherWire.Encode(operation);
        return AetherWire.EncodeContributions(
        [
            new StoredField(
                new RecordKey(operation.EntityId, operation.WriterId, change.Key),
                change.Value,
                new FieldVersion(operation.Lamport, operation.ActorId),
                operation.WriterId,
                operation.Id,
                operation.Sequence),
        ]);
    }

    private static AetherOperation CreationOperation(StoredEntity entity) => new(
        entity.OperationId,
        entity.EntityId,
        entity.CreatorId,
        entity.Version.ActorId,
        entity.Sequence,
        entity.Version.Lamport,
        new Dictionary<string, FieldValue>(),
        AetherEffect.CreateEntity,
        entityTypeId: entity.EntityTypeId,
        creatorId: entity.CreatorId,
        ownerId: entity.OwnerId,
        authority: entity.Authority);

    private static AetherOperation DeletionOperation(StoredDeletion deletion) => new(
        deletion.OperationId,
        deletion.EntityId,
        deletion.WriterId,
        deletion.Version.ActorId,
        deletion.Sequence,
        deletion.Version.Lamport,
        new Dictionary<string, FieldValue>(),
        AetherEffect.DeleteEntity,
        authority: deletion.Authority);

    private static AetherOperation OwnershipOperation(StoredOwnership row) => new(
        row.OperationId,
        row.EntityId,
        row.PreviousOwnerId,
        row.Version.ActorId,
        row.Sequence,
        row.Version.Lamport,
        new Dictionary<string, FieldValue>(),
        AetherEffect.TransferOwnership,
        newOwnerId: row.OwnerId,
        priorOwnerOperationId: row.PriorOperationId,
        authority: row.Authority);

    private AetherOperation SealAuthority(AetherOperation operation)
    {
        if (_principalSigner is null
            || !_principalSigner.TrySignPrincipal(operation.AuthorityPayload(), out var publicKey, out var signature))
            return operation;
        return AetherAuthority.Seal(operation, publicKey, signature);
    }

    private static bool IsMissingDependency(string? reason) =>
        string.Equals(reason, "Delete entity owner is unknown.", StringComparison.Ordinal)
        || string.Equals(reason, "Delete is not issued by the entity owner.", StringComparison.Ordinal)
        || string.Equals(reason, "Ownership transfer entity is unknown.", StringComparison.Ordinal)
        || string.Equals(reason, "Ownership transfer is not issued by the entity owner.", StringComparison.Ordinal)
        || string.Equals(reason, "Ownership transfer prior operation does not match.", StringComparison.Ordinal)
        || string.Equals(reason, "Hide entity owner is unknown.", StringComparison.Ordinal)
        || string.Equals(reason, "Publication entity owner is unknown.", StringComparison.Ordinal);

    private readonly record struct HeldOperation(string PrincipalId, AetherOperation Operation);

    private readonly record struct HeldContribution(string PrincipalId, IReadOnlyList<StoredField> Fields);
}
