namespace Ape.Core.Graph;

/// <summary>Authoring-time graph node. Registers stages into <see cref="GraphBuilder{TScratch}"/> at compile time only.</summary>
public abstract class Component<TScratch>
{
    public abstract string Id { get; }

    internal abstract void Register(GraphBuilder<TScratch> builder, string qualifiedPrefix);
}

/// <summary>Leaf component wrapping a single <see cref="IStage{TScratch}"/>.</summary>
public sealed class StageComponent<TScratch> : Component<TScratch>
{
    private readonly IStage<TScratch> _stage;

    public StageComponent(IStage<TScratch> stage) => _stage = stage ?? throw new ArgumentNullException(nameof(stage));

    public override string Id => _stage.Id;

    internal override void Register(GraphBuilder<TScratch> builder, string qualifiedPrefix) =>
        builder.AddStage(qualifiedPrefix, _stage);
}
