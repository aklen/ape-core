namespace Ape.Core.Scene.Commit;

/// <summary>
/// Plugin-facing registration only. Host ticks stay on Core-internal <c>IHostFrameRunner</c> /
/// <c>IDeterministicHostTick</c> so a plugin cannot drive <c>RaiseHostFrame</c>.
/// </summary>
public interface IFrameParticipantRegistry
{
    /// <summary>Adds a participant. Ids must be unique. Execution order is Phase → Order → ParticipantId, not registration order.</summary>
    void Register(IDeterministicFrameParticipant participant);
}
