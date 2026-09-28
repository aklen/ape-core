namespace Ape.Core.Replication;

/// <summary>
/// Extension methods for thread-safe operations on Replica objects.
/// </summary>
public static class ReplicaLockExtensions
{
    public static bool WithLock<T>(this T? replica, Action<T> action) where T : IReplica
    {
        if (replica == null)
            return false;

        lock (replica.SyncRoot)
        {
            action(replica);
        }
        return true;
    }

    public static TResult WithLock<T, TResult>(this T? replica, Func<T, TResult> func, TResult defaultValue = default!) where T : IReplica
    {
        if (replica == null)
            return defaultValue;

        lock (replica.SyncRoot)
        {
            return func(replica);
        }
    }

    public static bool TryWithLock<T>(this T? replica, Action<T> action) where T : IReplica
    {
        return WithLock(replica, action);
    }
}
