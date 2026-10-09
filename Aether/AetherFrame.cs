using Ape.Core.Graph;

namespace Ape.Core.Aether;

/// <summary>
/// Frozen frame around <see cref="AetherReducer"/>. The stages call that reducer. They do not keep a second copy of the rules.
/// </summary>
public struct AetherScratch
{
    public AetherReducer Reducer;
    public List<AetherOperation> Inbox;
    public string EntityId;
    public string LabelFieldId;
    public string SumFieldId;
    public string? ResolvedLabel;
    public long ResolvedSum;
}

public static class AetherFrame
{
    public static FrozenPlan<AetherScratch> Compile() =>
        new ComponentGraph<AetherScratch>("aether")
            .Add(new ApplyStage())
            .Add(new ResolveStage())
            .Hsm(h => h.State("Run", "apply", "resolve"))
            .Compile();

    private sealed class ApplyStage : IStage<AetherScratch>
    {
        public string Id => "apply";

        public void Execute(ref AetherScratch scratch)
        {
            foreach (var op in scratch.Inbox)
                scratch.Reducer.Apply(op, observeClock: true);
            scratch.Inbox.Clear();
        }
    }

    private sealed class ResolveStage : IStage<AetherScratch>
    {
        public string Id => "resolve";

        public void Execute(ref AetherScratch scratch)
        {
            scratch.ResolvedLabel = scratch.Reducer.ResolveLww(scratch.EntityId, scratch.LabelFieldId)?.Text;
            if (!string.IsNullOrEmpty(scratch.SumFieldId))
                scratch.ResolvedSum = scratch.Reducer.ResolveSum(scratch.EntityId, scratch.SumFieldId);
        }
    }
}
