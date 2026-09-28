namespace Ape.Core.Scene;

/// <summary>
/// Core apply window for Scene mutations. Stored in <see cref="ThreadStaticAttribute"/> so
/// apply rights do not flow to another <see cref="Thread"/> or <c>Task.Run</c> (not AsyncLocal).
/// Plugin threads, callbacks, and <see cref="ISceneRead"/> objects must not write;
/// <see cref="Ape.Core.Scene.Commit.SceneCommitApplicator"/> and network restore do.
/// Remaining bypass: in-place mutation of mutable reference-valued properties on types that
/// are not yet frozen snapshots (collections and nested model objects that never call <c>SetProperty</c>).
/// </summary>
public static class SceneMutationScope
{
    [ThreadStatic]
    private static int _depth;

    public static bool IsActive => _depth > 0;

    internal static IDisposable Begin()
    {
        _depth++;
        return new Lease(Environment.CurrentManagedThreadId);
    }

    internal static void ThrowIfInactive()
    {
        if (_depth <= 0)
            throw new SceneMutationOutsideScopeException();
    }

    private sealed class Lease : IDisposable
    {
        private readonly int _ownerThreadId;
        private bool _disposed;

        public Lease(int ownerThreadId)
        {
            _ownerThreadId = ownerThreadId;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException(
                    "Scene mutation scope must be disposed on its creating thread.");
            }

            _disposed = true;

            if (_depth <= 0)
                throw new InvalidOperationException("Unbalanced Scene mutation scope.");

            _depth--;
        }
    }
}

/// <summary>
/// Thrown when a replica/scene write happens outside <see cref="SceneMutationScope"/>.
/// </summary>
public sealed class SceneMutationOutsideScopeException : InvalidOperationException
{
    public SceneMutationOutsideScopeException()
        : base(
            "Scene mutations are only allowed inside Core apply scope (commit applicator or network restore).")
    {
    }
}
