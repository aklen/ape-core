using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ape.Core.Network;

/// <summary>
/// A stored 256-bit writer id for one profile directory.
/// The file stays the same across starts of that profile.
/// One process holds the profile. The file has a magic, a version, and a checksum.
/// A damaged file does not open. This value is not a public-key fingerprint and is not authentication.
/// </summary>
public sealed class DurablePrincipal : IDisposable
{
    public const int ByteLength = 32;
    public const string InvalidFile = "Principal file is invalid.";

    private const ushort FormatVersion = 1;
    private const int ChecksumLength = 32;
    private const int PrefixLength = 4 + 2 + ByteLength;
    private const int FileLength = PrefixLength + ChecksumLength;

    private static ReadOnlySpan<byte> Magic => "APR1"u8;

    private readonly FileStream _lock;
    private bool _disposed;

    private DurablePrincipal(FileStream gate, string id)
    {
        _lock = gate;
        Id = id;
    }

    public string Id { get; }

    public static DurablePrincipal Open(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileDirectory);
        Directory.CreateDirectory(profileDirectory);
        var gate = OpenGate(Path.Combine(profileDirectory, "principal.lock"));
        try
        {
            var id = LoadOrCreate(Path.Combine(profileDirectory, "principal"));
            return new DurablePrincipal(gate, id);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lock.Dispose();
    }

    private static FileStream OpenGate(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("Profile is already open.");
        }
    }

    private static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return Read(File.ReadAllBytes(path));

        var created = RandomNumberGenerator.GetBytes(ByteLength);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encode(created));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path);
        return ToText(created);
    }

    private static byte[] Encode(byte[] id)
    {
        var file = new byte[FileLength];
        Magic.CopyTo(file);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), FormatVersion);
        id.CopyTo(file.AsSpan(6));
        SHA256.HashData(file.AsSpan(0, PrefixLength)).CopyTo(file.AsSpan(PrefixLength));
        return file;
    }

    private static string Read(byte[] file)
    {
        if (file.Length != FileLength || !file.AsSpan(0, 4).SequenceEqual(Magic))
            throw new IOException(InvalidFile);
        if (BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4)) != FormatVersion)
            throw new IOException(InvalidFile);
        var checksum = SHA256.HashData(file.AsSpan(0, PrefixLength));
        if (!CryptographicOperations.FixedTimeEquals(checksum, file.AsSpan(PrefixLength, ChecksumLength)))
            throw new IOException(InvalidFile);
        return ToText(file.AsSpan(6, ByteLength).ToArray());
    }

    private static string ToText(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
