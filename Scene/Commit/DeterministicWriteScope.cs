namespace Ape.Core.Scene.Commit;

/// <summary>
/// Thread/async context for deterministic scene writes: <see cref="DeterministicSceneWrites"/> enqueue to the active <see cref="ISceneCommitSink"/>
/// (the host's <see cref="IFrameCommitBatch"/> during <see cref="IDeterministicFrameParticipant.OnHostFrame"/>).
/// </summary>
public sealed class DeterministicWriteScope : IDisposable
{
    private static readonly AsyncLocal<ScopeState?> Current = new();

    private readonly ScopeState? _previous;

    private DeterministicWriteScope(ScopeState state)
    {
        _previous = Current.Value;
        Current.Value = state;
    }

    /// <summary>Begins a scope; dispose to restore the previous scope (including nested scopes).</summary>
    public static DeterministicWriteScope Begin(ISceneCommitSink sink, long frameId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new DeterministicWriteScope(new ScopeState(sink, frameId));
    }

    internal static ScopeState? TryGetCurrent() => Current.Value;

    public void Dispose()
    {
        Current.Value = _previous;
    }

    internal readonly record struct ScopeState(ISceneCommitSink Sink, long FrameId);
}
