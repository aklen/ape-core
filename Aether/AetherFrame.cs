using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Scene.Commit;

namespace Ape.Core.Aether;

/// <summary>
/// Frozen frame around <see cref="AetherReducer"/>. The stages call that reducer. They do not keep a second copy of the rules.
/// </summary>
public struct AetherScratch
{
    public AetherReducer Reducer;
    public AetherHost? Host;
    public int FrameOperationBudget;
    public int FrameByteBudget;
    public List<AetherOperation> Inbox;
    public string EntityId;
    public string LabelFieldId;
    public string SumFieldId;
    public string? ResolvedLabel;
    public long ResolvedSum;
    public string? PublicationId;
    public bool ResolvedDeleted;
    public bool ResolvedSharedVisible;
    public bool ResolvedMember;
    public string? SceneEntityKey;
    public string? LabelPropertyName;
    public string? SumPropertyName;
    public List<ISceneCommitRequest>? SceneCommits;
    public List<AetherOutcome>? Outcomes;
}

public static class AetherFrame
{
    public static FrozenPlan<AetherScratch> Compile() =>
        new ComponentGraph<AetherScratch>("aether")
            .Add(new ApplyStage())
            .Add(new ResolveStage())
            .Add(new SceneCommitStage())
            .Hsm(h => h.State("Run", "apply", "resolve", "commit"))
            .Compile();

    private sealed class ApplyStage : IStage<AetherScratch>
    {
        public string Id => "apply";

        public void Execute(ref AetherScratch scratch)
        {
            scratch.Outcomes ??= new List<AetherOutcome>();
            scratch.Outcomes.Clear();
            if (scratch.Host is not null)
            {
                scratch.Reducer = scratch.Host.Reducer;
                scratch.Outcomes.AddRange(scratch.Host.Drain(scratch.FrameOperationBudget, scratch.FrameByteBudget));
                return;
            }

            foreach (var op in scratch.Inbox)
            {
                if (!AetherReducer.HasActorSequence(op))
                    continue;
                try
                {
                    scratch.Reducer.Apply(op, observeClock: true);
                }
                catch (AetherProtocolException)
                {
                    continue;
                }

                scratch.Reducer.RestoreActorSequence(op.ActorId, op.Sequence);
            }
            scratch.Inbox.Clear();
        }
    }

    private sealed class ResolveStage : IStage<AetherScratch>
    {
        public string Id => "resolve";

        public void Execute(ref AetherScratch scratch)
        {
            scratch.ResolvedDeleted = scratch.Reducer.IsDeleted(scratch.EntityId);
            scratch.ResolvedSharedVisible = scratch.Reducer.IsSharedVisible(scratch.EntityId);
            var publicationChosen = !string.IsNullOrEmpty(scratch.PublicationId);
            scratch.ResolvedMember = publicationChosen
                && scratch.Reducer.IsMember(scratch.EntityId, scratch.PublicationId!);
            if (!publicationChosen || scratch.ResolvedDeleted || !scratch.ResolvedSharedVisible || !scratch.ResolvedMember)
            {
                scratch.ResolvedLabel = null;
                scratch.ResolvedSum = 0;
                return;
            }

            scratch.ResolvedLabel = scratch.Reducer.ResolveLww(scratch.EntityId, scratch.LabelFieldId)?.Text;
            if (!string.IsNullOrEmpty(scratch.SumFieldId))
                scratch.ResolvedSum = scratch.Reducer.ResolveSum(scratch.EntityId, scratch.SumFieldId);
        }
    }

    private sealed class SceneCommitStage : IStage<AetherScratch>
    {
        public string Id => "commit";

        public void Execute(ref AetherScratch scratch)
        {
            scratch.SceneCommits ??= new List<ISceneCommitRequest>();
            scratch.SceneCommits.Clear();
            if (string.IsNullOrEmpty(scratch.SceneEntityKey))
                return;

            if (!string.IsNullOrEmpty(scratch.LabelPropertyName))
            {
                scratch.SceneCommits.Add(new SetSceneEntityPropertyCommitRequest(
                    scratch.SceneEntityKey,
                    scratch.LabelPropertyName,
                    scratch.ResolvedLabel));
            }

            if (!string.IsNullOrEmpty(scratch.SumPropertyName))
            {
                scratch.SceneCommits.Add(new SetSceneEntityPropertyCommitRequest(
                    scratch.SceneEntityKey,
                    scratch.SumPropertyName,
                    scratch.ResolvedSum));
            }
        }
    }
}
