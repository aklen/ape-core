namespace Ape.Core.Scene.Commit;

/// <summary>
/// When two participants write the same scene property in one frame.
/// Default is last-writer-wins (participant order) with a warning.
/// </summary>
public enum SceneMultiWriterMode
{
    /// <summary>Log a warning; later participant in Phase/Order/Id order wins.</summary>
    LastWriterWins = 0,

    /// <summary>Discard the whole frame (no scene apply). Needs an explicit arbitration policy to proceed.</summary>
    Strict = 1
}
