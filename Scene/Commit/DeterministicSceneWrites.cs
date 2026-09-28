using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Enqueues <see cref="ISceneCommitRequest"/> while a <see cref="DeterministicWriteScope"/> is active (e.g. from deterministic frame processors).
/// </summary>
public static class DeterministicSceneWrites
{
    /// <summary>
    /// Enqueue any commit (create/remove node/entity, or generic property sets).
    /// </summary>
    /// <exception cref="InvalidOperationException">No active <see cref="DeterministicWriteScope"/>.</exception>
    public static void Enqueue(ISceneCommitRequest request)
    {
        var state = DeterministicWriteScope.TryGetCurrent()
            ?? throw new InvalidOperationException(
                "No active DeterministicWriteScope. Wrap host frame processing with DeterministicWriteScope.Begin(sink, frameId).");
        state.Sink.Enqueue(request);
    }

    /// <summary>Enqueue <see cref="SetSceneNodePropertyCommitRequest"/>.</summary>
    public static void RecordNodeProperty(string sceneKey, string propertyName, object? value) =>
        Enqueue(new SetSceneNodePropertyCommitRequest(sceneKey, propertyName, value));

    /// <summary>Enqueue <see cref="SetSceneEntityPropertyCommitRequest"/>.</summary>
    public static void RecordEntityProperty(string sceneKey, string propertyName, object? value) =>
        Enqueue(new SetSceneEntityPropertyCommitRequest(sceneKey, propertyName, value));
}
