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

    public void Pump()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
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
                if (outcome.Kind != AetherOutcomeKind.Applied || outcome.Result is null)
                    continue;
                _outgoing.Enqueue(outcome.Result.Value.Operation);
            }
        }

        while (_outgoing.Count > 0)
        {
            _network.Broadcast(AetherWire.Encode(_outgoing.Peek()), reliable: true);
            _outgoing.Dequeue();
        }
    }

    public void Dispose()
    {
        if (_stopped)
            return;
        _stopped = true;
        _network.OnDataReceived -= OnData;
    }

    private void OnData(string peerId, byte[] data)
    {
        if (_stopped || !AetherWire.IsStatePacket(data))
            return;
        if (!AetherWire.TryDecode(data, _entityId, _fieldId, out var operation, out var fault))
        {
            LastRemoteFault = fault;
            LastRemoteAdmit = null;
            return;
        }

        LastRemoteFault = null;
        LastRemoteAdmit = _host.TryAcceptRemote(operation!);
    }
}
