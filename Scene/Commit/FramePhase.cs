namespace Ape.Core.Scene.Commit;

/// <summary>
/// Host-frame execution band. Participants run <c>Phase → Order → ParticipantId</c> (ordinal).
/// Fusion pipelines use <see cref="Transform"/>.
/// </summary>
public enum FramePhase
{
    Ingest = 0,
    Transform = 1,
    Publish = 2
}
