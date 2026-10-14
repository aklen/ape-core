using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Ape.Core.Graph;

namespace Ape.Core.Aether;

/// <summary>
/// One existing entity label, two peers, on <see cref="INetworkManager"/>.
/// A resolved value written back to the scene is not submitted again.
/// </summary>
public sealed class AetherLabelLink : IDisposable
{
    private readonly INetworkManager _network;
    private readonly IPrincipalSigner? _principalSigner;
    private readonly AetherHost _host;
    private readonly ISceneManager _scene;
    private readonly ILogger _logger;
    private readonly string _entityId;
    private readonly string _writerId;
    private readonly string _actorId;
    private readonly string _fieldId;
    private readonly int _maxOutgoing;
    private readonly int _maxUnidentifiedBytes;
    private readonly TimeProvider _time;
    private readonly TimeSpan _unidentifiedLifetime;
    private readonly bool _allowUnauthenticated;
    private readonly Func<AetherOperation, AetherOperation>? _authoritySeal;
    private readonly HashSet<long> _localIds = new();
    private readonly Queue<AetherOperation> _outgoing = new();
    private readonly Queue<HeldOperation> _held = new();
    private readonly Queue<HeldContribution> _heldContributions = new();
    private readonly Queue<UnidentifiedPacket> _unidentified = new();
    private int _unidentifiedBytes;
    private AetherOperation? _own;
    private AetherOperation? _ownCreate;
    private AetherOperation? _ownDelete;
    private AetherOperation? _ownTransfer;
    private byte[]? _contribution;
    private bool _catchUpPending;
    private bool _createCatchUpSent;
    private bool _deleteCatchUpSent;
    private bool _transferCatchUpSent;
    private bool _labelCatchUpSent;
    private readonly FrozenPlan<AetherScratch> _plan;
    private PlanRuntime _runtime;
    private AetherScratch _scratch;
    private bool _stopped;

    public AetherLabelLink(
        INetworkManager network,
        AetherHost host,
        ISceneManager scene,
        ILogger logger,
        string entityId,
        string writerId,
        string sessionId,
        string publicationId,
        string fieldId = "label",
        string labelPropertyName = "Label",
        int maxOutgoing = 64,
        int maxUnidentifiedBytes = 65_536,
        TimeProvider? time = null,
        TimeSpan? unidentifiedLifetime = null,
        bool allowUnauthenticated = false)
    {
        if (maxOutgoing < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOutgoing));
        if (maxUnidentifiedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxUnidentifiedBytes));
        if (unidentifiedLifetime is { } lifetime && lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(unidentifiedLifetime));
        _maxOutgoing = maxOutgoing;
        _maxUnidentifiedBytes = maxUnidentifiedBytes;
        _time = time ?? TimeProvider.System;
        _unidentifiedLifetime = unidentifiedLifetime ?? TimeSpan.FromSeconds(5);
        _allowUnauthenticated = allowUnauthenticated;
        _principalSigner = network as IPrincipalSigner;
        _authoritySeal = _principalSigner?.HasPrincipalSigner == true ? SealAuthority : null;
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _entityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        _writerId = writerId ?? throw new ArgumentNullException(nameof(writerId));
        _actorId = AetherActorId.Format(writerId, sessionId);
        _fieldId = fieldId ?? throw new ArgumentNullException(nameof(fieldId));
        if (scene.GetEntity(entityId) is Replica replica)
            replica.YieldPropertyToAether(labelPropertyName);
        NoteStoredEntity();

        var storedCreate = host.Reducer.GetEntity(entityId);
        if (storedCreate is not null && string.Equals(storedCreate.CreatorId, writerId, StringComparison.Ordinal))
            _ownCreate = CreationOperation(storedCreate);
        var storedDelete = host.Reducer.GetDeletion(entityId);
        if (storedDelete is not null && string.Equals(storedDelete.WriterId, writerId, StringComparison.Ordinal))
            _ownDelete = DeletionOperation(storedDelete);
        var storedOwnership = host.Reducer.GetOwnership(entityId);
        if (storedOwnership is not null && string.Equals(storedOwnership.PreviousOwnerId, writerId, StringComparison.Ordinal))
            _ownTransfer = OwnershipOperation(storedOwnership);

        _plan = AetherFrame.Compile();
        _runtime = new PlanRuntime();
        _plan.InitRuntime(ref _runtime);
        _scratch = new AetherScratch
        {
            Host = host,
            Reducer = host.Reducer,
            FrameOperationBudget = 64,
            FrameByteBudget = 1_000_000,
            EntityId = entityId,
            LabelFieldId = fieldId,
            PublicationId = publicationId,
            SceneEntityKey = entityId,
            LabelPropertyName = labelPropertyName,
        };
        network.OnDataReceived += OnData;
        network.OnPeerConnected += OnPeerConnected;
        network.OnPeerDisconnected += OnPeerDisconnected;
        network.OnPeerIdentified += OnPeerIdentified;
    }

    public AetherAdmit? LastRemoteAdmit { get; private set; }

    public string? LastRemoteFault { get; private set; }

    public AetherAdmit Submit(string label)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;

        var admit = _host.TryAcceptLocal(_entityId, _writerId, _actorId, new Dictionary<string, FieldValue>
        {
            [_fieldId] = FieldValue.Label(label),
        });
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    public AetherAdmit SubmitCreate(string entityTypeId)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocalCreate(_entityId, entityTypeId, _writerId, _actorId, _authoritySeal);
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    public AetherAdmit SubmitDelete()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocalDelete(_entityId, _writerId, _actorId, _authoritySeal);
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    public AetherAdmit SubmitOwnershipTransfer(string newOwnerId)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return AetherAdmit.Full;
        var admit = _host.TryAcceptLocalTransfer(_entityId, newOwnerId, _writerId, _actorId, _authoritySeal);
        if (admit.Kind == AetherAdmitKind.Queued)
            _localIds.Add(admit.RequestId);
        return admit;
    }

    public void CatchUp()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        _catchUpPending = true;
        _createCatchUpSent = _ownCreate is null;
        _deleteCatchUpSent = _ownDelete is null;
        _transferCatchUpSent = _ownTransfer is null;
        _labelCatchUpSent = _own is null && OwnRecords().Count == 0;
        ScheduleCatchUp();
        Flush();
        ScheduleContribution();
        FlushContribution();
    }

    public void Pump()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        DropExpired();
        AdmitHeld();
        AdmitContributions();
        _plan.Tick(ref _scratch, ref _runtime);
        // A Create or ownership transfer folded by this tick can unlock the next held link in the chain.
        // Queue it now; the frame budget still applies because it is folded by a later tick.
        AdmitHeld();
        NoteStoredEntity();
        if (_scratch.SceneCommits is not null)
        {
            foreach (var request in _scratch.SceneCommits)
                SceneCommitApplicator.Apply(_scene, request, _logger);
        }
        if (_scene.GetEntity(_entityId) is Replica projected)
            projected.YieldPropertyToAether(_scratch.LabelPropertyName ?? "Label");

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
                _outgoing.Enqueue(operation);
            }
        }

        Flush();
        ScheduleCatchUp();
        Flush();
        ScheduleContribution();
        FlushContribution();
    }

    public void Dispose()
    {
        if (_stopped)
            return;
        _stopped = true;
        _network.OnPeerConnected -= OnPeerConnected;
        _network.OnPeerDisconnected -= OnPeerDisconnected;
        _network.OnPeerIdentified -= OnPeerIdentified;
        _network.OnDataReceived -= OnData;
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
        if (!_network.TryGetPeer(connectionId, out var peer))
        {
            HoldUntilIdentified(connectionId, data);
            return;
        }

        if (!Accepts(peer))
        {
            RefuseUnauthenticated(connectionId);
            return;
        }

        AcceptRemote(peer.PrincipalId, peer.IsAuthenticated, operation, fields);
    }

    private void OnPeerConnected(string peerId) => CatchUp();

    private void OnPeerDisconnected(string connectionId)
    {
        if (_stopped)
            return;
        DropUnidentified(connectionId);
    }

    private void OnPeerIdentified(PeerConnectionContext peer)
    {
        if (_stopped)
            return;
        if (!Accepts(peer))
        {
            RefuseUnauthenticated(peer.ConnectionId);
            return;
        }

        DropExpired();
        var waiting = _unidentified.Count;
        for (var i = 0; i < waiting; i++)
        {
            var held = _unidentified.Dequeue();
            if (!string.Equals(held.ConnectionId, peer.ConnectionId, StringComparison.Ordinal))
            {
                _unidentified.Enqueue(held);
                continue;
            }

            _unidentifiedBytes -= held.Data.Length;
            if (!TryReadRemote(held.Data, out var operation, out var fields))
                continue;
            AcceptRemote(peer.PrincipalId, peer.IsAuthenticated, operation, fields);
        }
    }

    private bool Accepts(PeerConnectionContext peer) => peer.IsAuthenticated || _allowUnauthenticated;

    private void RefuseUnauthenticated(string connectionId)
    {
        DropUnidentified(connectionId);
        LastRemoteFault = "Peer is not authenticated.";
        LastRemoteAdmit = null;
    }

    private void DropUnidentified(string connectionId)
    {
        var kept = new Queue<UnidentifiedPacket>();
        while (_unidentified.Count > 0)
        {
            var held = _unidentified.Dequeue();
            if (string.Equals(held.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                _unidentifiedBytes -= held.Data.Length;
                continue;
            }

            kept.Enqueue(held);
        }

        while (kept.Count > 0)
            _unidentified.Enqueue(kept.Dequeue());
    }

    private void HoldUntilIdentified(string connectionId, byte[] data)
    {
        DropExpired();
        if (_unidentified.Count >= _maxOutgoing
            || data.Length > _maxUnidentifiedBytes
            || _unidentifiedBytes > _maxUnidentifiedBytes - data.Length)
        {
            LastRemoteFault = "Catch-up does not fit.";
            return;
        }

        LastRemoteFault = null;
        _unidentifiedBytes += data.Length;
        _unidentified.Enqueue(new UnidentifiedPacket(connectionId, data, _time.GetUtcNow()));
    }

    private void DropExpired()
    {
        if (_unidentified.Count == 0)
            return;
        var now = _time.GetUtcNow();
        var kept = new Queue<UnidentifiedPacket>();
        while (_unidentified.Count > 0)
        {
            var held = _unidentified.Dequeue();
            if (now - held.ArrivedAt > _unidentifiedLifetime)
            {
                _unidentifiedBytes -= held.Data.Length;
                continue;
            }

            kept.Enqueue(held);
        }

        while (kept.Count > 0)
            _unidentified.Enqueue(kept.Dequeue());
    }

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

            if (!string.Equals(decoded[0].Key.EntityId, _entityId, StringComparison.Ordinal))
            {
                LastRemoteFault = "Entity is not on this link.";
                LastRemoteAdmit = null;
                return false;
            }

            foreach (var field in decoded)
            {
                if (!string.Equals(field.Key.FieldId, _fieldId, StringComparison.Ordinal))
                {
                    LastRemoteFault = "Field is not on this link.";
                    LastRemoteAdmit = null;
                    return false;
                }
            }

            fields = decoded;
            return true;
        }

        if (!AetherWire.TryDecode(data, _entityId, _fieldId, out operation, out var operationFault))
        {
            LastRemoteFault = operationFault;
            LastRemoteAdmit = null;
            return false;
        }

        return true;
    }

    private void AcceptRemote(
        string principalId,
        bool authenticated,
        AetherOperation? operation,
        IReadOnlyList<StoredField>? fields)
    {
        if (fields is not null)
        {
            QueueContribution(principalId, fields);
            return;
        }

        LastRemoteFault = null;
        var admit = _host.TryAcceptRemote(operation!, principalId, requireAuthorityProof: authenticated);
        LastRemoteAdmit = admit;
        if (admit.Kind == AetherAdmitKind.Refused)
        {
            LastRemoteFault = admit.Reason;
            if (IsMissingDependency(admit.Reason))
                HoldOperation(principalId, authenticated, operation!);
            return;
        }

        if (admit.Kind == AetherAdmitKind.Queued)
            return;

        HoldOperation(principalId, authenticated, operation!);
    }

    private void HoldOperation(string principalId, bool authenticated, AetherOperation operation)
    {
        if (_held.Any(held => held.Operation.Id == operation.Id))
            return;
        if (_held.Count >= _maxOutgoing)
        {
            LastRemoteFault = "Catch-up does not fit.";
            return;
        }

        _held.Enqueue(new HeldOperation(principalId, authenticated, operation));
    }

    private void AdmitHeld()
    {
        var waiting = _held.Count;
        for (var i = 0; i < waiting; i++)
        {
            var held = _held.Dequeue();
            var admit = _host.TryAcceptRemote(held.Operation, held.PrincipalId, held.Authenticated);
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

    private void NoteStoredEntity()
    {
        if (_host.Reducer.GetEntity(_entityId) is null && _host.Reducer.GetDeletion(_entityId) is null)
            return;
        if (_scene is SceneManager manager)
            manager.NoteAetherEntity(_entityId);
    }

    private void Note(AetherOperation operation)
    {
        if (!string.Equals(operation.WriterId, _writerId, StringComparison.Ordinal)
            || operation.Effect is not (AetherEffect.Write or AetherEffect.CreateEntity or AetherEffect.DeleteEntity or AetherEffect.TransferOwnership))
            return;

        if (operation.Effect == AetherEffect.CreateEntity)
        {
            if (_ownCreate is null
                || new FieldVersion(operation.Lamport, operation.ActorId) > new FieldVersion(_ownCreate.Lamport, _ownCreate.ActorId))
                _ownCreate = operation;
            return;
        }
        if (operation.Effect == AetherEffect.DeleteEntity)
        {
            _ownDelete = operation;
            return;
        }
        if (operation.Effect == AetherEffect.TransferOwnership)
        {
            _ownTransfer = operation;
            return;
        }
        if (!operation.Changes.TryGetValue(_fieldId, out var value) || !value.IsText)
            return;

        var version = new FieldVersion(operation.Lamport, operation.ActorId);
        if (_own is not null && new FieldVersion(_own.Lamport, _own.ActorId).CompareTo(version) >= 0)
            return;
        _own = operation;
    }

    private void ScheduleCatchUp()
    {
        if (!_catchUpPending)
            return;
        if (!_createCatchUpSent && _ownCreate is not null
            && !_outgoing.Any(queued => queued.Id == _ownCreate.Id)
            && _localIds.Count + _outgoing.Count + (_contribution is null ? 0 : 1) < _maxOutgoing)
            _outgoing.Enqueue(_ownCreate);
        if (!_deleteCatchUpSent && _ownDelete is not null
            && !_outgoing.Any(queued => queued.Id == _ownDelete.Id)
            && _localIds.Count + _outgoing.Count + (_contribution is null ? 0 : 1) < _maxOutgoing)
            _outgoing.Enqueue(_ownDelete);
        if (!_transferCatchUpSent && _ownTransfer is not null
            && !_outgoing.Any(queued => queued.Id == _ownTransfer.Id)
            && _localIds.Count + _outgoing.Count + (_contribution is null ? 0 : 1) < _maxOutgoing)
            _outgoing.Enqueue(_ownTransfer);
        if (!_labelCatchUpSent && _own is not null
            && !_outgoing.Any(queued => queued.Id == _own.Id)
            && _localIds.Count + _outgoing.Count + (_contribution is null ? 0 : 1) < _maxOutgoing)
            _outgoing.Enqueue(_own);
    }

    private void Flush()
    {
        while (_outgoing.Count > 0)
        {
            var operation = _outgoing.Peek();
            _network.Broadcast(AetherWire.Encode(operation), reliable: true);
            _outgoing.Dequeue();
            if (_catchUpPending && _ownCreate is not null && operation.Id == _ownCreate.Id)
                _createCatchUpSent = true;
            if (_catchUpPending && _ownDelete is not null && operation.Id == _ownDelete.Id)
                _deleteCatchUpSent = true;
            if (_catchUpPending && _ownTransfer is not null && operation.Id == _ownTransfer.Id)
                _transferCatchUpSent = true;
            if (_catchUpPending && _own is not null && operation.Id == _own.Id)
                _labelCatchUpSent = true;
            if (_catchUpPending && _createCatchUpSent && _transferCatchUpSent && _deleteCatchUpSent && _labelCatchUpSent)
                _catchUpPending = false;
        }
    }

    private void ScheduleContribution()
    {
        if (!_catchUpPending
            || !_createCatchUpSent
            || !_deleteCatchUpSent
            || !_transferCatchUpSent
            || _own is not null
            || _contribution is not null)
            return;
        var records = OwnRecords();
        if (records.Count == 0)
            return;
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return;
        try
        {
            _contribution = AetherWire.EncodeContributions(records);
        }
        catch (AetherProtocolException ex) when (ex.Message == AetherWire.PacketTooLarge)
        {
            LastRemoteFault = ex.Message;
            _catchUpPending = false;
        }
    }

    private void FlushContribution()
    {
        if (_contribution is null)
            return;
        var packet = _contribution;
        _network.Broadcast(packet, reliable: true);
        _contribution = null;
        _labelCatchUpSent = true;
        if (_createCatchUpSent && _transferCatchUpSent && _deleteCatchUpSent)
            _catchUpPending = false;
    }

    private List<StoredField> OwnRecords()
    {
        var records = new List<StoredField>();
        foreach (var field in _host.Reducer.Capture().Fields)
        {
            if (!string.Equals(field.Key.EntityId, _entityId, StringComparison.Ordinal)
                || !string.Equals(field.Key.WriterId, _writerId, StringComparison.Ordinal)
                || !string.Equals(field.Key.FieldId, _fieldId, StringComparison.Ordinal)
                || !field.Value.IsText)
                continue;
            records.Add(field);
        }

        return records;
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
        row.OperationId, row.EntityId, row.PreviousOwnerId, row.Version.ActorId, row.Sequence, row.Version.Lamport,
        new Dictionary<string, FieldValue>(), AetherEffect.TransferOwnership,
        newOwnerId: row.OwnerId, priorOwnerOperationId: row.PriorOperationId, authority: row.Authority,
        frozenVisibilityOperationId: row.FrozenVisibilityOperationId,
        frozenMembershipOperationIds: row.FrozenMembershipOperationIds);

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
        || string.Equals(reason, "Ownership transfer prior operation does not match.", StringComparison.Ordinal);

    private readonly record struct HeldOperation(string PrincipalId, bool Authenticated, AetherOperation Operation);

    private readonly record struct HeldContribution(string PrincipalId, IReadOnlyList<StoredField> Fields);

    private readonly record struct UnidentifiedPacket(string ConnectionId, byte[] Data, DateTimeOffset ArrivedAt);
}
