using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ape.Core.Aether;

/// <summary>
/// Local snapshot plus a short recovery log. Saving writes a temporary file, flushes it, then replaces the current file.
/// The frame does not call this.
/// </summary>
public sealed class AetherArchive
{
    public const string SnapshotFileName = "snapshot.bin";
    public const string LogFileName = "recovery.log";
    private const ushort FormatVersion = 1;

    private static ReadOnlySpan<byte> SnapshotMagic => "AET1"u8;
    private static ReadOnlySpan<byte> LogMagic => "AEL1"u8;

    private readonly string _directory;

    public AetherArchive(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Archive directory is missing.", nameof(directory));
        _directory = directory;
    }

    public string SnapshotPath => Path.Combine(_directory, SnapshotFileName);

    public string LogPath => Path.Combine(_directory, LogFileName);

    public void Save(AetherReducer reducer) => Save(reducer, commitReplacement: true);

    internal void Save(AetherReducer reducer, bool commitReplacement)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        Directory.CreateDirectory(_directory);
        var generation = ReadGeneration(SnapshotPath) + 1;
        var body = EncodeSnapshotBody(reducer);
        WriteDurable(SnapshotPath, Finish(SnapshotMagic, generation, body), commitReplacement);
        if (!commitReplacement)
            return;
        WriteDurable(LogPath, Finish(LogMagic, generation, []), commitReplacement: true);
    }

    public void Append(AetherOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (!File.Exists(SnapshotPath))
            throw new AetherProtocolException("No snapshot to append after.");

        var generation = ReadGeneration(SnapshotPath);
        if (!File.Exists(LogPath) || ReadGeneration(LogPath) != generation)
            throw new AetherProtocolException("Recovery log does not match the snapshot.");

        var payload = EncodeOperation(op);
        var record = new byte[4 + payload.Length + 32];
        BinaryPrimitives.WriteUInt32BigEndian(record, (uint)payload.Length);
        payload.CopyTo(record.AsSpan(4));
        SHA256.HashData(payload).CopyTo(record.AsSpan(4 + payload.Length));
        using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(record);
        stream.Flush(flushToDisk: true);
    }

    public AetherReducer Load()
    {
        if (!File.Exists(SnapshotPath))
            throw new AetherProtocolException("No snapshot to load.");

        var snapshot = ReadFramed(SnapshotPath, SnapshotMagic);
        var reader = new Reader(snapshot.Body);
        var capacity = checked((int)reader.U32());
        if (capacity < 1)
            throw new AetherProtocolException("Snapshot dedup capacity is invalid.");

        var reducer = new AetherReducer(capacity);
        var clock = reader.I64();
        var schemaCount = reader.U32();
        for (var i = 0; i < schemaCount; i++)
        {
            var id = reader.Text();
            var kind = reader.U8();
            var min = reader.I64();
            var max = reader.I64();
            var resolver = kind switch
            {
                1 => "lww",
                2 => "sumContributions",
                _ => throw new AetherProtocolException("Snapshot field kind is unknown."),
            };
            reducer.DefineField(id, resolver, min, max);
        }

        var fields = ReadFields(reader);
        var seen = ReadSeen(reader);
        var deleted = ReadTexts(reader);
        var visibility = ReadVisibility(reader);
        var membership = ReadMembership(reader);
        var cursors = ReadCursors(reader);
        reader.End();

        reducer.MergeImage(new AetherImage(clock, fields, seen, deleted, visibility, membership));
        foreach (var cursor in cursors)
            reducer.RestoreActorSequence(cursor.ActorId, cursor.Sequence);

        if (File.Exists(LogPath) && ReadGeneration(LogPath) == snapshot.Generation)
            Replay(reducer, File.ReadAllBytes(LogPath));

        return reducer;
    }

    private static void Replay(AetherReducer reducer, byte[] log)
    {
        var framed = Open(log, LogMagic);
        var hashOffset = framed.BodyOffset + framed.Body.Length;
        if (log.Length < hashOffset + 32)
            throw new AetherProtocolException("Recovery log is truncated.");
        if (!SHA256.HashData(framed.Body).AsSpan().SequenceEqual(log.AsSpan(hashOffset, 32)))
            throw new AetherProtocolException("Recovery log checksum failed.");

        var reader = new Reader(log);
        reader.Skip(hashOffset + 32);
        while (reader.Remaining > 0)
        {
            if (reader.Remaining < 4)
                return;
            var length = reader.U32();
            if (reader.Remaining < length + 32)
                return;
            var payload = reader.Bytes((int)length);
            var hash = reader.Bytes(32);
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
                throw new AetherProtocolException("Recovery log checksum failed.");

            var op = DecodeOperation(payload);
            reducer.RestoreActorSequence(op.ActorId, op.Sequence);
            reducer.Apply(op, observeClock: true);
        }
    }

    private static List<StoredField> ReadFields(Reader reader)
    {
        var count = reader.U32();
        var fields = new List<StoredField>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            var entityId = reader.Text();
            var writerId = reader.Text();
            var fieldId = reader.Text();
            var operationId = reader.Text();
            var lamport = reader.I64();
            var actorId = reader.Text();
            var kind = reader.U8();
            var fixedValue = reader.I64();
            var text = reader.Text();
            var value = kind switch
            {
                1 => FieldValue.FixedPoint(fixedValue),
                2 => FieldValue.Label(text),
                _ => throw new AetherProtocolException("Snapshot value kind is unknown."),
            };
            fields.Add(new StoredField(
                new RecordKey(entityId, writerId, fieldId),
                value,
                new FieldVersion(lamport, actorId),
                writerId,
                operationId));
        }

        return fields;
    }

    private static List<SeenOperation> ReadSeen(Reader reader)
    {
        var count = reader.U32();
        var seen = new List<SeenOperation>(checked((int)count));
        for (var i = 0; i < count; i++)
            seen.Add(new SeenOperation(reader.Text(), reader.Text(), reader.I64()));
        return seen;
    }

    private static List<string> ReadTexts(Reader reader)
    {
        var count = reader.U32();
        var values = new List<string>(checked((int)count));
        for (var i = 0; i < count; i++)
            values.Add(reader.Text());
        return values;
    }

    private static List<VisibilityMark> ReadVisibility(Reader reader)
    {
        var count = reader.U32();
        var marks = new List<VisibilityMark>(checked((int)count));
        for (var i = 0; i < count; i++)
            marks.Add(new VisibilityMark(reader.Text(), new FieldVersion(reader.I64(), reader.Text()), reader.U8() == 1));
        return marks;
    }

    private static List<MembershipMark> ReadMembership(Reader reader)
    {
        var count = reader.U32();
        var marks = new List<MembershipMark>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            var entityId = reader.Text();
            var publicationId = reader.Text();
            var version = new FieldVersion(reader.I64(), reader.Text());
            marks.Add(new MembershipMark(entityId, publicationId, version, reader.U8() == 1));
        }

        return marks;
    }

    private static List<ActorCursor> ReadCursors(Reader reader)
    {
        var count = reader.U32();
        var cursors = new List<ActorCursor>(checked((int)count));
        for (var i = 0; i < count; i++)
            cursors.Add(new ActorCursor(reader.Text(), reader.I64()));
        return cursors;
    }

    private static byte[] EncodeSnapshotBody(AetherReducer reducer)
    {
        var image = reducer.Capture();
        using var buffer = new MemoryStream();
        WriteU32(buffer, (uint)reducer.DedupCapacity);
        WriteI64(buffer, image.Clock);
        WriteU32(buffer, (uint)reducer.Schema.Count);
        foreach (var field in reducer.Schema)
        {
            WriteText(buffer, field.Id);
            buffer.WriteByte(field.Resolver == "lww" ? (byte)1 : (byte)2);
            WriteI64(buffer, field.Min);
            WriteI64(buffer, field.Max);
        }

        WriteU32(buffer, (uint)image.Fields.Count);
        foreach (var field in image.Fields)
        {
            WriteText(buffer, field.Key.EntityId);
            WriteText(buffer, field.Key.WriterId);
            WriteText(buffer, field.Key.FieldId);
            WriteText(buffer, field.OperationId);
            WriteI64(buffer, field.Version.Lamport);
            WriteText(buffer, field.Version.ActorId);
            buffer.WriteByte(field.Value.IsText ? (byte)2 : (byte)1);
            WriteI64(buffer, field.Value.Fixed);
            WriteText(buffer, field.Value.Text ?? "");
        }

        WriteU32(buffer, (uint)image.Seen.Count);
        foreach (var seen in image.Seen)
        {
            WriteText(buffer, seen.Id);
            WriteText(buffer, seen.Digest);
            WriteI64(buffer, seen.Lamport);
        }

        WriteTexts(buffer, image.Deleted);
        WriteU32(buffer, (uint)(image.Visibility?.Count ?? 0));
        foreach (var mark in image.Visibility ?? [])
        {
            WriteText(buffer, mark.EntityId);
            WriteI64(buffer, mark.Version.Lamport);
            WriteText(buffer, mark.Version.ActorId);
            buffer.WriteByte(mark.Hidden ? (byte)1 : (byte)0);
        }

        WriteU32(buffer, (uint)(image.Membership?.Count ?? 0));
        foreach (var mark in image.Membership ?? [])
        {
            WriteText(buffer, mark.EntityId);
            WriteText(buffer, mark.PublicationId);
            WriteI64(buffer, mark.Version.Lamport);
            WriteText(buffer, mark.Version.ActorId);
            buffer.WriteByte(mark.Published ? (byte)1 : (byte)0);
        }

        WriteU32(buffer, (uint)reducer.ActorCursors.Count);
        foreach (var cursor in reducer.ActorCursors)
        {
            WriteText(buffer, cursor.ActorId);
            WriteI64(buffer, cursor.Sequence);
        }

        return buffer.ToArray();
    }

    private static byte[] EncodeOperation(AetherOperation op)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)op.Effect);
        WriteText(buffer, op.Id);
        WriteText(buffer, op.EntityId);
        WriteText(buffer, op.WriterId);
        WriteText(buffer, op.ActorId);
        WriteText(buffer, op.PublicationId ?? "");
        WriteI64(buffer, op.Sequence);
        WriteI64(buffer, op.Lamport);
        WriteU32(buffer, (uint)op.Changes.Count);
        foreach (var (fieldId, value) in op.Changes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteText(buffer, fieldId);
            buffer.WriteByte(value.IsText ? (byte)2 : (byte)1);
            WriteI64(buffer, value.Fixed);
            WriteText(buffer, value.Text ?? "");
        }

        return buffer.ToArray();
    }

    private static AetherOperation DecodeOperation(byte[] payload)
    {
        var reader = new Reader(payload);
        var effect = (AetherEffect)reader.U8();
        var id = reader.Text();
        var entityId = reader.Text();
        var writerId = reader.Text();
        var actorId = reader.Text();
        var publicationId = reader.Text();
        var sequence = reader.I64();
        var lamport = reader.I64();
        var count = reader.U32();
        var changes = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var fieldId = reader.Text();
            var kind = reader.U8();
            var fixedValue = reader.I64();
            var text = reader.Text();
            changes[fieldId] = kind switch
            {
                1 => FieldValue.FixedPoint(fixedValue),
                2 => FieldValue.Label(text),
                _ => throw new AetherProtocolException("Recovery log value kind is unknown."),
            };
        }

        reader.End();
        return new AetherOperation(
            id,
            entityId,
            writerId,
            actorId,
            sequence,
            lamport,
            changes,
            effect,
            effect is AetherEffect.Publish or AetherEffect.WithdrawPublication ? publicationId : null);
    }

    private static byte[] Finish(ReadOnlySpan<byte> magic, long generation, byte[] body)
    {
        var framed = new byte[4 + 2 + 8 + 4 + body.Length + 32];
        magic.CopyTo(framed);
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt64BigEndian(framed.AsSpan(6), generation);
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(14), (uint)body.Length);
        body.CopyTo(framed.AsSpan(18));
        SHA256.HashData(body).CopyTo(framed.AsSpan(18 + body.Length));
        return framed;
    }

    private static void WriteDurable(string path, byte[] bytes, bool commitReplacement)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!commitReplacement)
            return;
        File.Move(temporary, path, overwrite: true);
    }

    private static long ReadGeneration(string path)
    {
        if (!File.Exists(path))
            return 0;
        return Open(File.ReadAllBytes(path), default).Generation;
    }

    private static Framed ReadFramed(string path, ReadOnlySpan<byte> magic)
    {
        var bytes = File.ReadAllBytes(path);
        var framed = Open(bytes, magic);
        var hashOffset = framed.BodyOffset + framed.Body.Length;
        if (bytes.Length < hashOffset + 32)
            throw new AetherProtocolException("Snapshot is truncated.");
        if (bytes.Length != hashOffset + 32)
            throw new AetherProtocolException("Snapshot has trailing bytes.");
        if (!SHA256.HashData(framed.Body).AsSpan().SequenceEqual(bytes.AsSpan(hashOffset, 32)))
            throw new AetherProtocolException("Snapshot checksum failed.");
        return framed;
    }

    private static Framed Open(byte[] bytes, ReadOnlySpan<byte> magic)
    {
        if (bytes.Length < 18)
            throw new AetherProtocolException("Archive file is truncated.");
        if (!magic.IsEmpty && !bytes.AsSpan(0, 4).SequenceEqual(magic))
            throw new AetherProtocolException("Archive file magic is unknown.");
        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        if (version != FormatVersion)
            throw new AetherProtocolException($"Archive format {version} is unknown.");
        var generation = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(6));
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(14));
        if (bytes.Length < 18 + length)
            throw new AetherProtocolException("Archive file is truncated.");
        return new Framed(generation, bytes.AsSpan(18, (int)length).ToArray(), 18);
    }

    private static void WriteTexts(Stream buffer, IReadOnlyList<string>? values)
    {
        WriteU32(buffer, (uint)(values?.Count ?? 0));
        foreach (var value in values ?? [])
            WriteText(buffer, value);
    }

    private static void WriteText(Stream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteU32(buffer, (uint)bytes.Length);
        buffer.Write(bytes);
    }

    private static void WriteU32(Stream buffer, uint value)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        buffer.Write(encoded);
    }

    private static void WriteI64(Stream buffer, long value)
    {
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, value);
        buffer.Write(encoded);
    }

    private readonly record struct Framed(long Generation, byte[] Body, int BodyOffset);

    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private int _offset;

        public Reader(byte[] bytes) => _bytes = bytes;

        public int Remaining => _bytes.Length - _offset;

        public void Skip(int count) => _offset += count;

        public byte U8() => Bytes(1)[0];

        public uint U32()
        {
            var bytes = Bytes(4);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        public long I64()
        {
            var bytes = Bytes(8);
            return BinaryPrimitives.ReadInt64BigEndian(bytes);
        }

        public string Text()
        {
            var length = U32();
            return Encoding.UTF8.GetString(Bytes(checked((int)length)));
        }

        public byte[] Bytes(int count)
        {
            if (count < 0 || _offset > _bytes.Length - count)
                throw new AetherProtocolException("Archive file is truncated.");
            var slice = _bytes.AsSpan(_offset, count).ToArray();
            _offset += count;
            return slice;
        }

        public void End()
        {
            if (_offset != _bytes.Length)
                throw new AetherProtocolException("Archive body has trailing bytes.");
        }
    }
}
