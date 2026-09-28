using Ape.Core.Determinism;
using Ape.Core.Scene.Commit;

namespace Ape.Core.Runtime.Plugin;

/// <summary>
/// Plugin-facing registry that is not the host tick / commit service, so a cast cannot recover <see cref="IHostFrameRunner"/>.
/// </summary>
internal sealed class FrameParticipantRegistryFacade : IFrameParticipantRegistry
{
    private readonly IFrameParticipantRegistry _inner;

    public FrameParticipantRegistryFacade(IFrameParticipantRegistry inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public void Register(IDeterministicFrameParticipant participant) => _inner.Register(participant);
}
