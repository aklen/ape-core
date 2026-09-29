namespace Ape.Core.Graph;

/// <summary>Flattened runtime unit: one <see cref="Execute"/> per host tick when enabled by the plan HSM.</summary>
public interface IStage<TScratch>
{
    string Id { get; }

    void Execute(ref TScratch scratch);

    /// <summary>
    /// Declares scratch reads/writes for the compiler. Default is empty (permissive mode):
    /// undeclared access is invisible to <c>CG207</c> and leaf <c>CG208</c> — not proof of no access.
    /// Strict closed-world checking is a later compiler mode, not the hot path.
    /// </summary>
    void Describe(StageContract<TScratch> contract)
    {
    }
}
