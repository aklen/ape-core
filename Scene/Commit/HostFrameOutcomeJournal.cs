using System.Text;
using System.Text.Json;
using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>Append-only host outcome sidecar; pair with a SAMPLE journal by frame id.</summary>
public static class HostFrameOutcomeJournal
{
    private const int Version = 1;

    private sealed record Row(int FormatVersion, string Kind, HostFrameRecord? Outcome = null);

    public static Writer CreateNew(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Writer(path);
    }

    public static IReadOnlyList<HostFrameRecord> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = ReadRow(reader.ReadLine(), 1);
        if (header.Kind != "header" || header.Outcome is not null)
            throw new InvalidDataException("Host outcome journal must start with a header row.");

        var outcomes = new List<HostFrameRecord>();
        long? lastId = null;
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var row = ReadRow(line, lineNumber);
            if (row.Kind != "outcome" || row.Outcome is null)
                throw new InvalidDataException($"Invalid host outcome row at line {lineNumber}.");
            Validate(row.Outcome, lastId);
            outcomes.Add(row.Outcome);
            lastId = row.Outcome.FrameId;
        }
        return outcomes;
    }

    /// <summary>Require one outcome for each recorded SAMPLE frame, including failed/discarded frames.</summary>
    public static void VerifyAlignment(
        IReadOnlyList<SampleFrame> samples,
        IReadOnlyList<HostFrameRecord> outcomes)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (samples.Count != outcomes.Count)
            throw new InvalidDataException("SAMPLE and host outcome journals have different frame counts.");
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].FrameId != outcomes[i].FrameId ||
                samples[i].LogicalTime != outcomes[i].LogicalTime)
                throw new InvalidDataException($"SAMPLE and host outcome mismatch at row {i + 1}.");
        }
    }

    private static Row ReadRow(string? line, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidDataException($"Missing host outcome row at line {lineNumber}.");
        try
        {
            var row = JsonSerializer.Deserialize<Row>(line);
            if (row is null || row.FormatVersion != Version)
                throw new InvalidDataException($"Unsupported host outcome format at line {lineNumber}.");
            return row;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Malformed host outcome row at line {lineNumber}.", ex);
        }
    }

    private static void Validate(HostFrameRecord outcome, long? lastId)
    {
        if (outcome.FrameId < 0 || lastId.HasValue && outcome.FrameId <= lastId.Value)
            throw new InvalidDataException("Host outcome frame ids must increase strictly.");
        if (!Enum.IsDefined(outcome.Status))
            throw new InvalidDataException("Unknown host frame status.");
    }

    public sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        private long? _lastId;
        private bool _disposed;

        internal Writer(string path)
        {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true);
            WriteRow(new Row(Version, "header"));
        }

        public void Append(HostFrameRecord outcome)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(outcome);
            Validate(outcome, _lastId);
            WriteRow(new Row(Version, "outcome", outcome));
            _lastId = outcome.FrameId;
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
