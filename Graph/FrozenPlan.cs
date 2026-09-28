namespace Ape.Core.Graph;

/// <summary>
/// Flattened execution plan, frozen at plugin initialization. The authoring graph can be discarded after
/// <see cref="ComponentGraph{TScratch}.Compile"/>. Stage order and HSM tables never change while ticking.
/// </summary>
public sealed partial class FrozenPlan<TScratch>
{
    private readonly IStage<TScratch>[] _stages;
    private readonly Dictionary<string, int> _stageIndex;
    private readonly HsmDefinition _hsm;
    private readonly Dictionary<string, ulong> _stateMasks;
    private readonly IReadOnlyList<string> _connections;
    private readonly IReadOnlyList<(string SourcePath, string EventId)> _raises;
    private readonly IReadOnlyList<PortIR> _ports;
    private readonly IReadOnlyList<StageAccessIR> _access;

    private FrozenPlan(
        IStage<TScratch>[] stages,
        Dictionary<string, int> stageIndex,
        HsmDefinition hsm,
        Dictionary<string, ulong> stateMasks,
        IReadOnlyList<string> connections,
        IReadOnlyList<(string SourcePath, string EventId)> raises,
        IReadOnlyList<PortIR> ports,
        IReadOnlyList<StageAccessIR> access)
    {
        _stages = stages;
        _stageIndex = stageIndex;
        _hsm = hsm;
        _stateMasks = stateMasks;
        _connections = connections;
        _raises = raises;
        _ports = ports;
        _access = access;
    }

    public string Name { get; private init; } = "";

    public IReadOnlyList<string> StageOrder { get; private init; } = [];

    public IReadOnlyList<string> Connections => _connections;

    public IReadOnlyList<string> HsmStates => _hsm.States;

    internal static FrozenPlan<TScratch> From(GraphDefinition<TScratch> def)
    {
        var stages = def.Stages.ToArray();
        var stageIndex = new Dictionary<string, int>(def.StageIndexByQualifiedId, StringComparer.Ordinal);
        var stageOrder = stageIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
        if (stages.Length > 64)
        {
            throw new GraphCompileException(
                "CG106",
                $"FrozenPlan '{def.Name}' has {stages.Length} stages; the enable mask currently supports at most 64.");
        }

        var enableByTarget = BuildEnableMap(stageIndex);
        var stateMasks = BuildStateMasks(def.Hsm, enableByTarget);

        return new FrozenPlan<TScratch>(
            stages,
            stageIndex,
            def.Hsm,
            stateMasks,
            def.Connections.Select(c => $"{c.From} -> {c.To}").ToArray(),
            def.Raises,
            def.Ports,
            def.Access)
        {
            Name = def.Name,
            StageOrder = stageOrder,
        };
    }

    public void InitRuntime(ref PlanRuntime rt) => ApplyState(_hsm.InitialState, ref rt);

    /// <summary>
    /// One host frame: drain the event queue as control microsteps (each event replaces the mask),
    /// then execute only the stages enabled by the <em>final</em> state.
    /// Events enqueued during <see cref="IStage{TScratch}.Execute"/> wait until the next frame.
    /// </summary>
    public void Tick(ref TScratch scratch, ref PlanRuntime rt)
    {
        while (rt.Events.Count > 0)
        {
            var ev = rt.Events.Dequeue();
            DispatchEvent(ev, ref rt);
        }

        for (var i = 0; i < _stages.Length; i++)
        {
            if (rt.IsStageEnabled(i))
                _stages[i].Execute(ref scratch);
        }
    }

    private void DispatchEvent(string eventId, ref PlanRuntime rt)
    {
        foreach (var t in _hsm.Transitions)
        {
            if (!string.Equals(t.EventId, eventId, StringComparison.Ordinal))
                continue;

            if (t.FromState != null && !string.Equals(t.FromState, rt.CurrentState, StringComparison.Ordinal))
                continue;

            ApplyState(t.ToState, ref rt);
            return;
        }
    }

    private void ApplyState(string state, ref PlanRuntime rt)
    {
        rt.CurrentState = state;
        rt.SetMask(_stateMasks.TryGetValue(state, out var mask) ? mask : 0);
    }

    internal bool IsDeclaredActive(int stageIndex, string state)
    {
        if (!_stateMasks.TryGetValue(state, out var mask))
            return false;
        return stageIndex >= 0 && stageIndex < 64 && ((mask >> stageIndex) & 1) != 0;
    }

    private static Dictionary<string, ulong> BuildStateMasks(
        HsmDefinition hsm,
        Dictionary<string, List<int>> enableByTarget)
    {
        var masks = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var state in hsm.States)
        {
            ulong mask = 0;
            if (hsm.ActiveByState.TryGetValue(state, out var targets))
            {
                foreach (var target in targets)
                {
                    if (!enableByTarget.TryGetValue(target, out var indices))
                        continue;
                    foreach (var idx in indices)
                        mask |= 1UL << idx;
                }
            }

            masks[state] = mask;
        }

        return masks;
    }

    private static Dictionary<string, List<int>> BuildEnableMap(Dictionary<string, int> stageIndex)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var (qualifiedId, index) in stageIndex)
        {
            AddTarget(map, qualifiedId, index);

            var slash = qualifiedId.LastIndexOf('/');
            while (slash >= 0)
            {
                var prefix = qualifiedId[..slash];
                AddTarget(map, prefix, index);
                slash = qualifiedId.LastIndexOf('/', slash - 1);
            }
        }

        return map;
    }

    private static void AddTarget(Dictionary<string, List<int>> map, string target, int index)
    {
        if (!map.TryGetValue(target, out var list))
        {
            list = new List<int>();
            map[target] = list;
        }

        list.Add(index);
    }
}
