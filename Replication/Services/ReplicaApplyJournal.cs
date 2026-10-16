using System.Text;

namespace Ape.Core.Replication.Services;

/// <summary>
/// Append-only record of replica registration and field updates, in the order they were applied.
/// </summary>
internal sealed class ReplicaApplyJournal : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private bool _disposed;

    private ReplicaApplyJournal(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true);
    }

    public static ReplicaApplyJournal CreateNew(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ReplicaApplyJournal(path);
    }

    public void Send(string id) => Line($"send\t{id}");

    public void Register(string id) => Line($"register\t{id}");

    public void Update(string id, string field) => Line($"update\t{id}\t{field}");

    private void Line(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.WriteLine(text);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writer.Dispose();
        _stream.Dispose();
    }
}
