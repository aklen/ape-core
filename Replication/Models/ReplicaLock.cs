using System.Diagnostics;

namespace Ape.Core.Replication;

/// <summary>
/// RAII-style lock helper for thread-safe access to Replica objects.
/// </summary>
/// <typeparam name="T">Type of replica to lock (Node, Light, Geometry, etc.)</typeparam>
public readonly struct ReplicaLock<T> : IDisposable where T : IReplica
{
    private readonly T? _replica;
    private readonly Stopwatch _stopwatch;
    private readonly bool _acquired;

    public T? Replica => _replica;

    public bool Acquired => _acquired;

    public ReplicaLock(T? replica, int timeoutMs = 5000)
    {
        _replica = replica;
        _stopwatch = Stopwatch.StartNew();
        _acquired = false;

        if (_replica == null)
        {
            return;
        }

        _acquired = Monitor.TryEnter(_replica.SyncRoot, timeoutMs);

        if (!_acquired)
        {
            Console.WriteLine($"[ReplicaLock] WARNING: Failed to acquire lock for {typeof(T).Name} '{_replica.UniquePath}' after {timeoutMs}ms - possible deadlock!");
        }
    }

    public void Dispose()
    {
        if (_replica == null || !_acquired)
        {
            return;
        }

        _stopwatch.Stop();

        if (_stopwatch.ElapsedMilliseconds > 100)
        {
            Console.WriteLine($"[ReplicaLock] PERF: Lock on {typeof(T).Name} '{_replica.UniquePath}' held for {_stopwatch.ElapsedMilliseconds}ms");
        }

        Monitor.Exit(_replica.SyncRoot);
    }
}
