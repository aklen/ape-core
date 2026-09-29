using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>Host-owned batch: valid only on the RaiseHostFrame thread until <see cref="Seal"/>.</summary>
internal sealed class FrameCommitBatch : IFrameCommitBatch
{
    private readonly int _hostThreadId;
    private readonly List<ISceneCommitRequest> _ops = new();
    private bool _sealed;

    public FrameCommitBatch(string participantId, int hostThreadId)
    {
        if (string.IsNullOrWhiteSpace(participantId))
            throw new ArgumentException("Participant id is required.", nameof(participantId));
        ParticipantId = participantId;
        _hostThreadId = hostThreadId;
    }

    public string ParticipantId { get; }

    internal IReadOnlyList<ISceneCommitRequest> Operations => _ops;

    public void Enqueue(ISceneCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_sealed)
            throw new InvalidOperationException(
                $"IFrameCommitBatch for '{ParticipantId}' is sealed. Scene writes belong in OnHostFrame COMMIT only.");
        if (Environment.CurrentManagedThreadId != _hostThreadId)
            throw new InvalidOperationException(
                "IFrameCommitBatch can only be used on the host thread during OnHostFrame. Callbacks enqueue to IIngress<T>.");
        _ops.Add(request);
    }

    public void Seal() => _sealed = true;
}
