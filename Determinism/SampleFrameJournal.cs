using System.Text;
using System.Text.Json;

namespace Ape.Core.Determinism;

/// <summary>
/// Versioned, append-only JSON-lines SAMPLE journal. A complete header is followed
/// by strictly increasing frame ids. This records inputs, not Scene output.
/// </summary>
public static class SampleFrameJournal
{
    private const int FormatVersion = 1;

    private sealed record Row(
        int Version,
        string Kind,
        SampleFrameJournalHeader? Header = null,
        SampleFrame? Frame = null);

    public static Writer CreateNew(string path, SampleFrameJournalHeader header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(header);
        return new Writer(path, header);
    }

    public static (SampleFrameJournalHeader Header, IReadOnlyList<SampleFrame> Frames) Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerRow = ReadRow(reader.ReadLine(), 1);
        if (headerRow.Kind != "header" || headerRow.Header is null || headerRow.Frame is not null)
            throw new InvalidDataException("SAMPLE journal must start with one header row.");

        var frames = new List<SampleFrame>();
        long? lastId = null;
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var row = ReadRow(line, lineNumber);
            if (row.Kind != "frame" || row.Frame is null || row.Header is not null)
                throw new InvalidDataException($"Invalid SAMPLE frame row at line {lineNumber}.");
            ValidateFrame(row.Frame, lastId);
            frames.Add(row.Frame);
            lastId = row.Frame.FrameId;
        }

        return (headerRow.Header, frames);
    }

    private static Row ReadRow(string? line, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidDataException($"Missing or empty SAMPLE journal row at line {lineNumber}.");
        try
        {
            var row = JsonSerializer.Deserialize<Row>(line);
            if (row is null || row.Version != FormatVersion)
                throw new InvalidDataException($"Unsupported SAMPLE journal version at line {lineNumber}.");
            return row;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Malformed SAMPLE journal row at line {lineNumber}.", ex);
        }
    }

    private static void ValidateFrame(SampleFrame frame, long? lastId)
    {
        if (frame.FrameId < 0 || lastId.HasValue && frame.FrameId <= lastId.Value)
            throw new InvalidDataException("SAMPLE journal frame ids must be non-negative and strictly increasing.");
        if (frame.Inputs is null)
            throw new InvalidDataException("SAMPLE journal frame inputs cannot be null.");
        foreach (var input in frame.Inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.SourceId) ||
                string.IsNullOrWhiteSpace(input.PayloadType) ||
                input.Payload is null && string.IsNullOrWhiteSpace(input.CasReference))
                throw new InvalidDataException("SAMPLE journal input needs a source, type, and inline payload or CAS reference.");
            if (input.Payload is null && string.IsNullOrWhiteSpace(input.PayloadHash))
                throw new InvalidDataException("CAS-backed SAMPLE input needs a payload hash.");
        }
    }

    public sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        private long? _lastId;
        private bool _disposed;

        internal Writer(string path, SampleFrameJournalHeader header)
        {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true);
            WriteRow(new Row(FormatVersion, "header", Header: header));
        }

        public void Append(SampleFrame frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(frame);
            ValidateFrame(frame, _lastId);
            WriteRow(new Row(FormatVersion, "frame", Frame: frame));
            _lastId = frame.FrameId;
        }

        private void WriteRow(Row row)
        {
            _writer.WriteLine(JsonSerializer.Serialize(row));
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
}
