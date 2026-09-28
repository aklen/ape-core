namespace Ape.Core.Graph;

internal sealed class GraphBuilder<TScratch>
{
    private readonly List<IStage<TScratch>> _stages = new();
    private readonly Dictionary<string, int> _stageIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PortIR> _ports = new(StringComparer.Ordinal);
    private readonly List<ConnectionIR> _connections = new();
    private readonly List<StageAccessIR> _access = new();
    private readonly List<(string SourcePath, string EventId)> _raises = new();
    private HsmDefinition _hsm = new()
    {
        InitialState = "Idle",
        States = ["Idle"],
        ActiveByState = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["Idle"] = [] },
        Transitions = [],
    };

    public string GraphName { get; }

    public GraphBuilder(string graphName) => GraphName = graphName;

    public void AddStage(string qualifiedId, IStage<TScratch> stage)
    {
        if (_stageIndex.ContainsKey(qualifiedId))
            throw new GraphCompileException("CG101", $"Duplicate stage id '{qualifiedId}'.");

        _stageIndex[qualifiedId] = _stages.Count;
        _stages.Add(stage);

        var contract = new StageContract<TScratch>();
        stage.Describe(contract);
        _access.Add(new StageAccessIR
        {
            StageId = qualifiedId,
            Reads = contract.ReadSlots.ToArray(),
            Writes = contract.WriteSlots.ToArray(),
        });
    }

    public void AddPort(PortIR port)
    {
        if (!_ports.TryAdd(port.QualifiedId, port))
        {
            throw new GraphCompileException(
                "CG210",
                $"Duplicate port '{port.QualifiedId}'.");
        }
    }

    public void AddConnection(string fromPath, string toPath, Type clrType) =>
        _connections.Add(new ConnectionIR { From = fromPath, To = toPath, ClrType = clrType });

    public void AddRaise(string sourcePath, string eventId) =>
        _raises.Add((sourcePath, eventId));

    public void SetHsm(HsmDefinition hsm) => _hsm = hsm;

    public GraphDefinition<TScratch> Build() => new()
    {
        Name = GraphName,
        Stages = _stages,
        StageIndexByQualifiedId = _stageIndex,
        Ports = _ports.Values.OrderBy(p => p.QualifiedId, StringComparer.Ordinal).ToArray(),
        Connections = _connections,
        Access = _access,
        Raises = _raises,
        Hsm = _hsm,
    };
}
