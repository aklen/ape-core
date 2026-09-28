using System.Linq.Expressions;

namespace Ape.Core.Graph;

/// <summary>
/// Authoring DSL for nested components. <see cref="Compile"/> freezes the graph at initialization
/// (not C# compile time) into a <see cref="FrozenPlan{TScratch}"/>. No structural mutation after that.
/// </summary>
public sealed class ComponentGraph<TScratch> : Component<TScratch>
{
    private readonly List<Component<TScratch>> _children = new();
    private readonly List<PortDecl> _ports = new();
    private readonly List<(string From, string To, Type ClrType)> _connections = new();
    private readonly List<(string Source, string EventId)> _raises = new();
    private Action<HsmBuilder>? _hsm;

    public ComponentGraph(string id) => Id = id;

    public override string Id { get; }

    public ComponentGraph<TScratch> Add(Component<TScratch> child)
    {
        _children.Add(child);
        return this;
    }

    public ComponentGraph<TScratch> Add(IStage<TScratch> stage) =>
        Add(new StageComponent<TScratch>(stage));

    public ComponentGraph<TScratch> Connect<T>(string fromPath, string toPath)
    {
        _connections.Add((fromPath, toPath, typeof(T)));
        return this;
    }

    public ComponentGraph<TScratch> Raise(string sourcePath, string eventId)
    {
        _raises.Add((sourcePath, eventId));
        return this;
    }

    public ComponentGraph<TScratch> Hsm(Action<HsmBuilder> configure)
    {
        _hsm = configure;
        return this;
    }

    public ComponentGraph<TScratch> ExposeIn<T>(Expression<Func<TScratch, T>> selector, string portName) =>
        Expose(PortDirection.In, selector, portName, ownerId: null);

    public ComponentGraph<TScratch> ExposeIn<T>(
        string ownerId,
        Expression<Func<TScratch, T>> selector,
        string portName) =>
        Expose(PortDirection.In, selector, portName, ownerId);

    public ComponentGraph<TScratch> ExposeOut<T>(Expression<Func<TScratch, T>> selector, string portName) =>
        Expose(PortDirection.Out, selector, portName, ownerId: null);

    public ComponentGraph<TScratch> ExposeOut<T>(
        string ownerId,
        Expression<Func<TScratch, T>> selector,
        string portName) =>
        Expose(PortDirection.Out, selector, portName, ownerId);

    public FrozenPlan<TScratch> Compile() => GraphCompiler.Compile(this);

    internal override void Register(GraphBuilder<TScratch> builder, string scopePrefix)
    {
        foreach (var child in _children)
        {
            if (child is ComponentGraph<TScratch> nested)
                nested.Register(builder, Combine(scopePrefix, nested.Id));
            else
                child.Register(builder, Combine(scopePrefix, child.Id));
        }

        foreach (var port in _ports)
        {
            var owner = port.OwnerId == null ? scopePrefix : Combine(scopePrefix, port.OwnerId);
            builder.AddPort(new PortIR
            {
                QualifiedId = Combine(owner, port.PortName),
                OwnerPrefix = owner,
                Direction = port.Direction,
                Freshness = port.Direction == PortDirection.In ? PortFreshness.Sampled : PortFreshness.CurrentTick,
                ClrType = port.ClrType,
                ScratchSlot = port.ScratchSlot,
            });
        }

        foreach (var (from, to, clrType) in _connections)
            builder.AddConnection(Qualify(scopePrefix, from), Qualify(scopePrefix, to), clrType);

        foreach (var (source, eventId) in _raises)
            builder.AddRaise(Qualify(scopePrefix, source), eventId);

        if (_hsm != null)
        {
            var hb = new HsmBuilder();
            _hsm(hb);
            builder.SetHsm(hb.Build());
        }
    }

    private ComponentGraph<TScratch> Expose<T>(
        PortDirection direction,
        Expression<Func<TScratch, T>> selector,
        string portName,
        string? ownerId)
    {
        _ports.Add(new PortDecl(direction, typeof(T), ScratchSlot.Name(selector), portName, ownerId));
        return this;
    }

    private static string Qualify(string prefix, string path) =>
        string.IsNullOrEmpty(prefix) ? path : Combine(prefix, path);

    private static string Combine(string prefix, string childId) =>
        string.IsNullOrEmpty(prefix) ? childId : $"{prefix}/{childId}";

    private readonly record struct PortDecl(
        PortDirection Direction,
        Type ClrType,
        string ScratchSlot,
        string PortName,
        string? OwnerId);
}
