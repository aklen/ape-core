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
    private readonly AetherHost _host;
    private readonly ISceneManager _scene;
    private readonly ILogger _logger;
    private readonly string _entityId;
    private readonly string _writerId;
    private readonly string _actorId;
    private readonly string _fieldId;
    private readonly int _maxOutgoing;
    private readonly HashSet<long> _localIds = new();
    private readonly Queue<AetherOperation> _outgoing = new();
    private readonly Queue<AetherOperation> _held = new();
    private readonly Queue<IReadOnlyList<StoredField>> _heldContributions = new();
    private AetherOperation? _own;
    private byte[]? _contribution;
    private bool _catchUpPending;
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
        string actorId,
        string publicationId,
        string fieldId = "label",
        string labelPropertyName = "Label",
        int maxOutgoing = 64)
    {
        if (maxOutgoing < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOutgoing));
        _maxOutgoing = maxOutgoing;
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _entityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        _writerId = writerId ?? throw new ArgumentNullException(nameof(writerId));
        _actorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
        _fieldId = fieldId ?? throw new ArgumentNullException(nameof(fieldId));
        if (scene.GetEntity(entityId) is not Replica replica)
            throw new InvalidOperationException("The scene entity must already exist.");
        replica.YieldPropertyToAether(labelPropertyName);

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

    public void CatchUp()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        _catchUpPending = true;
        if (_own is not null)
        {
            ScheduleCatchUp();
            Flush();
            return;
        }

        ScheduleContribution();
        FlushContribution();
    }

    public void Pump()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        AdmitHeld();
        AdmitContributions();
        _plan.Tick(ref _scratch, ref _runtime);
        if (_scratch.SceneCommits is not null)
        {
            foreach (var request in _scratch.SceneCommits)
                SceneCommitApplicator.Apply(_scene, request, _logger);
        }

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
        _network.OnDataReceived -= OnData;
    }

    private void OnData(string peerId, byte[] data)
    {
        if (_stopped || !AetherWire.IsStatePacket(data))
            return;
        if (AetherWire.IsContributionPacket(data))
        {
            HoldContribution(data);
            return;
        }
        if (!AetherWire.TryDecode(data, _entityId, _fieldId, out var operation, out var fault))
        {
            LastRemoteFault = fault;
            LastRemoteAdmit = null;
            return;
        }

        LastRemoteFault = null;
        var admit = _host.TryAcceptRemote(operation!);
        LastRemoteAdmit = admit;
        if (admit.Kind == AetherAdmitKind.Queued)
            return;

        if (_held.Any(held => held.Id == operation!.Id))
            return;
        if (_held.Count >= _maxOutgoing)
        {
            LastRemoteFault = "Catch-up does not fit.";
            return;
        }

        _held.Enqueue(operation!);
    }

    private void OnPeerConnected(string peerId) => CatchUp();

    private void AdmitHeld()
    {
        var waiting = _held.Count;
        for (var i = 0; i < waiting; i++)
        {
            var operation = _held.Dequeue();
            var admit = _host.TryAcceptRemote(operation);
            LastRemoteAdmit = admit;
            if (admit.Kind == AetherAdmitKind.Queued)
                continue;

            _held.Enqueue(operation);
        }
    }

    private void HoldContribution(byte[] data)
    {
        if (!AetherWire.TryDecodeContributions(data, out var fields, out var fault))
        {
            LastRemoteFault = fault;
            LastRemoteAdmit = null;
            return;
        }

        if (!string.Equals(fields[0].Key.EntityId, _entityId, StringComparison.Ordinal))
        {
            LastRemoteFault = "Entity is not on this link.";
            LastRemoteAdmit = null;
            return;
        }

        foreach (var field in fields)
        {
            if (!string.Equals(field.Key.FieldId, _fieldId, StringComparison.Ordinal))
            {
                LastRemoteFault = "Field is not on this link.";
                LastRemoteAdmit = null;
                return;
            }
        }

        if (_heldContributions.Count >= _maxOutgoing)
        {
            LastRemoteFault = "Catch-up does not fit.";
            return;
        }

        LastRemoteFault = null;
        _heldContributions.Enqueue(fields);
    }

    private void AdmitContributions()
    {
        var waiting = _heldContributions.Count;
        for (var i = 0; i < waiting; i++)
        {
            var fields = _heldContributions.Dequeue();
            var admit = _host.TryAcceptContribution(fields);
            LastRemoteAdmit = admit;
            if (admit.Kind == AetherAdmitKind.Queued)
                continue;

            _heldContributions.Enqueue(fields);
        }
    }

    private void Note(AetherOperation operation)
    {
        if (!string.Equals(operation.WriterId, _writerId, StringComparison.Ordinal)
            || operation.Effect != AetherEffect.Write
            || !operation.Changes.TryGetValue(_fieldId, out var value)
            || !value.IsText)
            return;

        var version = new FieldVersion(operation.Lamport, operation.ActorId);
        if (_own is not null && new FieldVersion(_own.Lamport, _own.ActorId).CompareTo(version) >= 0)
            return;
        _own = operation;
    }

    private void ScheduleCatchUp()
    {
        if (!_catchUpPending || _own is null)
            return;
        if (_outgoing.Any(queued => queued.Id == _own.Id))
            return;
        if (_localIds.Count + _outgoing.Count + (_contribution is null ? 0 : 1) >= _maxOutgoing)
            return;
        _outgoing.Enqueue(_own);
    }

    private void Flush()
    {
        while (_outgoing.Count > 0)
        {
            var operation = _outgoing.Peek();
            _network.Broadcast(AetherWire.Encode(operation), reliable: true);
            _outgoing.Dequeue();
            if (_catchUpPending && _own is not null && operation.Id == _own.Id)
                _catchUpPending = false;
        }
    }

    private void ScheduleContribution()
    {
        if (!_catchUpPending || _own is not null || _contribution is not null)
            return;
        var records = OwnRecords();
        if (records.Count == 0)
            return;
        if (_localIds.Count + _outgoing.Count >= _maxOutgoing)
            return;
        _contribution = AetherWire.EncodeContributions(records);
    }

    private void FlushContribution()
    {
        if (_contribution is null)
            return;
        var packet = _contribution;
        _network.Broadcast(packet, reliable: true);
        _contribution = null;
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
}
