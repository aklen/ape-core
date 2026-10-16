using System.Globalization;
using System.Numerics;
using System.Text;
using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Append-only record of scene commits in the order the host applied them.
/// One line per applied request. Frame timing is not part of the line, so two runs can be compared directly.
/// </summary>
internal sealed class SceneCommitJournal : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private bool _disposed;

    private SceneCommitJournal(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true);
    }

    public static SceneCommitJournal CreateNew(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new SceneCommitJournal(path);
    }

    public void Append(string participantId, ISceneCommitRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        ArgumentNullException.ThrowIfNull(request);
        _writer.WriteLine(Format(participantId, request));
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
    }

    public static string Format(string participantId, ISceneCommitRequest request) =>
        request switch
        {
            CreateSceneNodeCommitRequest op =>
                $"{participantId}\tcreate-node\t{op.NodeName}\t{op.OwnerId}\t{FormatVector(op.InitialPosition)}",
            SetSceneNodePositionCommitRequest op =>
                $"{participantId}\tset-position\t{op.NodeId}\t{FormatVector(op.Position)}",
            RemoveSceneNodeCommitRequest op =>
                $"{participantId}\tremove-node\t{op.NodeId}",
            SetSceneNodePropertyCommitRequest op =>
                $"{participantId}\tset-node\t{op.SceneKey}\t{op.PropertyName}\t{FormatValue(op.Value)}",
            CreateRegisteredEntityCommitRequest op =>
                $"{participantId}\tcreate-entity\t{op.TypeId}\t{op.Name}\t{op.OwnerId}",
            EnsureAetherEntityCommitRequest op =>
                $"{participantId}\tensure-entity\t{op.TypeId}\t{op.Name}\t{op.CreatorId}\t{op.OwnerId}",
            RemoveRegisteredEntityCommitRequest op =>
                $"{participantId}\tremove-entity\t{op.Name}",
            SetSceneEntityPropertyCommitRequest op =>
                $"{participantId}\tset-entity\t{op.SceneKey}\t{op.PropertyName}\t{FormatValue(op.Value)}",
            _ => $"{participantId}\tother\t{request.GetType().Name}"
        };

    private static string FormatVector(Vector3 value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.X},{value.Y},{value.Z}");

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        Vector3 vector => FormatVector(vector),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writer.Dispose();
        _stream.Dispose();
    }
}
