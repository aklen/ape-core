namespace Ape.Core.Graph;

internal static class GraphCompiler
{
    public static FrozenPlan<TScratch> Compile<TScratch>(ComponentGraph<TScratch> root)
    {
        var builder = new GraphBuilder<TScratch>(root.Id);
        root.Register(builder, "");
        var def = builder.Build();
        GraphValidator.Validate(def);
        return FrozenPlan<TScratch>.From(def);
    }
}
