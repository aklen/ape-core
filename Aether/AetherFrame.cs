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
    public StoredEntity? ResolvedEntity;
    public string? SceneEntityKey;
    public string? LabelPropertyName;
    public string? SumPropertyName;
    public bool ProjectScene;
    public List<AetherSceneRow>? SceneRows;
    public List<ISceneCommitRequest>? SceneCommits;
    public List<AetherOutcome>? Outcomes;
}

/// <summary>
/// One living or deleted entity projected by the scene link. The commit stage walks these rows in one tick.
/// </summary>
public readonly record struct AetherSceneRow(
    string EntityId,
    string TypeId,
    string CreatorId,
    string OwnerId,
    bool Deleted,
    string? Label,
    long Sum);

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
            if (scratch.ProjectScene)
            {
                ResolveScene(ref scratch);
                return;
            }

            scratch.ResolvedDeleted = scratch.Reducer.IsDeleted(scratch.EntityId);
            scratch.ResolvedEntity = scratch.Reducer.GetEntity(scratch.EntityId);
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

        private static void ResolveScene(ref AetherScratch scratch)
        {
            scratch.SceneRows ??= new List<AetherSceneRow>();
            scratch.SceneRows.Clear();
            foreach (var entity in scratch.Reducer.Entities)
            {
                var deleted = scratch.Reducer.IsDeleted(entity.EntityId);
                var visible = scratch.Reducer.IsSharedVisible(entity.EntityId);
                var member = !string.IsNullOrEmpty(scratch.PublicationId)
                    && scratch.Reducer.IsMember(entity.EntityId, scratch.PublicationId!);
                string? label = null;
                long sum = 0;
                if (!deleted && visible && member)
                {
                    label = scratch.Reducer.ResolveLww(entity.EntityId, scratch.LabelFieldId)?.Text;
                    if (!string.IsNullOrEmpty(scratch.SumFieldId))
                        sum = scratch.Reducer.ResolveSum(entity.EntityId, scratch.SumFieldId);
                }

                scratch.SceneRows.Add(new AetherSceneRow(
                    entity.EntityId,
                    entity.EntityTypeId,
                    entity.CreatorId,
                    scratch.Reducer.GetOwner(entity.EntityId) ?? entity.OwnerId,
                    deleted,
                    label,
                    sum));
            }
        }
    }

    private sealed class SceneCommitStage : IStage<AetherScratch>
    {
        public string Id => "commit";

        public void Execute(ref AetherScratch scratch)
        {
            scratch.SceneCommits ??= new List<ISceneCommitRequest>();
            scratch.SceneCommits.Clear();
            if (scratch.ProjectScene)
            {
                CommitScene(ref scratch);
                return;
            }

            if (string.IsNullOrEmpty(scratch.SceneEntityKey))
                return;

            if (scratch.ResolvedDeleted)
            {
                scratch.SceneCommits.Add(new RemoveRegisteredEntityCommitRequest(scratch.SceneEntityKey));
                return;
            }

            if (scratch.ResolvedEntity is { } entity)
            {
                scratch.SceneCommits.Add(new EnsureAetherEntityCommitRequest(
                    entity.EntityTypeId,
                    entity.EntityId,
                    entity.CreatorId,
                    scratch.Reducer.GetOwner(entity.EntityId)!));
            }

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

        private static void CommitScene(ref AetherScratch scratch)
        {
            if (scratch.SceneRows is null)
                return;

            foreach (var row in scratch.SceneRows)
            {
                if (row.Deleted)
                {
                    scratch.SceneCommits!.Add(new RemoveRegisteredEntityCommitRequest(row.EntityId));
                    continue;
                }

                scratch.SceneCommits!.Add(new EnsureAetherEntityCommitRequest(
                    row.TypeId,
                    row.EntityId,
                    row.CreatorId,
                    row.OwnerId));
                if (!string.IsNullOrEmpty(scratch.LabelPropertyName))
                {
                    scratch.SceneCommits.Add(new SetSceneEntityPropertyCommitRequest(
                        row.EntityId,
                        scratch.LabelPropertyName,
                        row.Label));
                }

                if (!string.IsNullOrEmpty(scratch.SumPropertyName))
                {
                    scratch.SceneCommits.Add(new SetSceneEntityPropertyCommitRequest(
                        row.EntityId,
                        scratch.SumPropertyName,
                        row.Sum));
                }
            }
        }
    }
}
