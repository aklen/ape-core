using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Ape.Core.Network;

/// <summary>
/// The key pair stored for one profile directory.
/// The principal id is the SHA-256 fingerprint of the public key.
/// One process holds the profile. The file has a magic, a version, and a checksum.
/// Format version 2 does not open a version 1 file. That file held a random id, which is not this key's fingerprint, so a new profile is required.
/// The key file is readable only by its owner. The checksum shows integrity, not secrecy.
/// The temporary key is flushed, then renamed, and the profile directory is flushed so the new name survives power loss.
/// A recovered temporary file is opened exclusively, checked as a canonical P-256 key, flushed again, and only then renamed.
/// An existing principal is accepted only after the profile directory is flushed again.
/// The parent of the profile directory must already exist. Open may create the profile directory, and it flushes that parent before it accepts the principal.
/// A damaged or shared file does not open. Possession of this key is not create, owner, or delete authority.
/// </summary>
public sealed class DurablePrincipal : IDisposable
{
    public const int ByteLength = 32;
    public const string InvalidFile = "Principal file is invalid.";
    public const string NotPrivate = "Principal file is not private.";
    public const string MissingParent = "Profile directory parent is missing.";

    private static readonly UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private const ushort FormatVersion = 2;
    private const int ChecksumLength = 32;
    private const int HeaderLength = 10;
    private const int MaxKeyBytes = 512;

    private static ReadOnlySpan<byte> Magic => "APR1"u8;

    [ThreadStatic]
    private static int _fileFlushFailures;
    [ThreadStatic]
    private static int _renameFailures;
    [ThreadStatic]
    private static int _directoryFlushFailures;
    [ThreadStatic]
    private static int _parentFlushFailures;

    private readonly FileStream _lock;
    private readonly ECDsa _key;
    private bool _disposed;

    private DurablePrincipal(FileStream gate, ECDsa key, byte[] publicKey, byte[] fingerprint)
    {
        _lock = gate;
        _key = key;
        PublicKey = publicKey;
        FingerprintBytes = fingerprint;
        Id = ToText(fingerprint);
    }

    public string Id { get; }

    internal byte[] FingerprintBytes { get; }

    internal byte[] PublicKey { get; }

    public static DurablePrincipal Open(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileDirectory);
        var full = Path.GetFullPath(profileDirectory);
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new IOException(MissingParent);
        Directory.CreateDirectory(full);
        FlushParent(parent);
        MakePrivateDirectory(full);
        var gate = OpenGate(Path.Combine(full, "principal.lock"));
        try
        {
            return LoadOrCreate(gate, Path.Combine(full, "principal"));
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
        _key.Dispose();
        _lock.Dispose();
    }

    internal byte[] Sign(ReadOnlySpan<byte> transcript) => _key.SignData(transcript, HashAlgorithmName.SHA256);

    internal static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var key = AcceptPublicKey(publicKey);
            if (key is null)
                return false;
            return key.VerifyData(transcript, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static bool IsCanonicalP256(ReadOnlySpan<byte> publicKey)
    {
        using var key = AcceptPublicKey(publicKey);
        return key is not null;
    }

    internal static void FailNextFlushes(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        _fileFlushFailures = count;
    }

    internal static void FailNextRenames(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        _renameFailures = count;
    }

    internal static void FailNextDirectoryFlushes(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        _directoryFlushFailures = count;
    }

    internal static void ClearFaults()
    {
        _fileFlushFailures = 0;
        _renameFailures = 0;
        _directoryFlushFailures = 0;
        _parentFlushFailures = 0;
    }

    internal static void FailNextParentFlushes(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        _parentFlushFailures = count;
    }

    internal static void ImportOwnedKey(ECDsa key, ReadOnlySpan<byte> pkcs8)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.ImportPkcs8PrivateKey(pkcs8, out var read);
        if (read != pkcs8.Length || !IsP256(key))
            throw new CryptographicException("Private key encoding is not canonical.");
    }

    private static ECDsa? AcceptPublicKey(ReadOnlySpan<byte> publicKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || !IsP256(key))
            {
                key.Dispose();
                return null;
            }

            var exported = key.ExportSubjectPublicKeyInfo();
            if (!exported.AsSpan().SequenceEqual(publicKey))
            {
                key.Dispose();
                return null;
            }

            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }

    private static bool IsP256(ECDsa key)
    {
        if (key.KeySize != 256)
            return false;
        var oid = key.ExportParameters(false).Curve.Oid;
        return string.Equals(oid.Value, P256Oid, StringComparison.Ordinal)
            || oid.FriendlyName is "nistP256" or "ECDSA_P256";
    }

    private static FileStream OpenGate(string path)
    {
        var existed = File.Exists(path);
        FileStream stream;
        try
        {
            stream = existed
                ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : OpenExclusive(path, FileMode.Create, FileAccess.ReadWrite);
        }
        catch (IOException ex) when (ex.Message != NotPrivate)
        {
            throw new IOException("Profile is already open.");
        }

        if (!existed)
            return stream;

        try
        {
            RequirePrivate(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static DurablePrincipal LoadOrCreate(FileStream gate, string path)
    {
        if (File.Exists(path))
        {
            RequirePrivate(path);
            FlushDirectory(Path.GetDirectoryName(path) ?? throw new IOException(InvalidFile));
            return Read(gate, File.ReadAllBytes(path));
        }

        var temporary = path + ".tmp";
        if (File.Exists(temporary) && TryRecover(temporary, out var recovered))
        {
            Publish(temporary, path);
            return Adopt(gate, recovered);
        }

        using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256) ?? throw new IOException(InvalidFile);
        var pkcs8 = created.ExportPkcs8PrivateKey();
        if (!IsCanonicalPrivateKey(pkcs8))
            throw new IOException(InvalidFile);
        using (var stream = OpenExclusive(temporary, FileMode.Create, FileAccess.Write))
        {
            stream.Write(Encode(pkcs8));
            stream.Flush();
            FlushFile(stream);
        }

        Publish(temporary, path);
        return Adopt(gate, pkcs8);
    }

    private static bool TryRecover(string temporary, out byte[] pkcs8)
    {
        pkcs8 = [];
        RequirePrivate(temporary);
        using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (!TryDecodeKey(ReadBounded(stream), out pkcs8))
            return false;
        if (!IsCanonicalPrivateKey(pkcs8))
            throw new IOException(InvalidFile);

        FlushFile(stream);
        return true;
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        if (stream.Length > HeaderLength + MaxKeyBytes + ChecksumLength)
            return [];
        var file = new byte[(int)stream.Length];
        stream.Position = 0;
        stream.ReadExactly(file);
        return file;
    }

    private static bool IsCanonicalPrivateKey(byte[] pkcs8)
    {
        try
        {
            using var key = LoadCanonicalKey(pkcs8);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void Publish(string temporary, string path)
    {
        if (_renameFailures > 0)
        {
            _renameFailures--;
            throw new IOException("Rename failed.");
        }

        File.Move(temporary, path);
        try
        {
            FlushDirectory(Path.GetDirectoryName(path) ?? throw new IOException(InvalidFile));
            RequirePrivate(path);
        }
        catch (IOException ex) when (ex.Message == NotPrivate)
        {
            File.Delete(path);
            throw;
        }
    }

    private static void FlushFile(FileStream stream)
    {
        if (_fileFlushFailures > 0)
        {
            _fileFlushFailures--;
            throw new IOException("Flush failed.");
        }

        stream.Flush(flushToDisk: true);
    }

    private static void FlushParent(string directory)
    {
        if (_parentFlushFailures > 0)
        {
            _parentFlushFailures--;
            throw new IOException("Flush failed.");
        }

        SyncDirectory(directory);
    }

    private static void FlushDirectory(string directory)
    {
        if (_directoryFlushFailures > 0)
        {
            _directoryFlushFailures--;
            throw new IOException("Flush failed.");
        }

        SyncDirectory(directory);
    }

    private static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            DirectorySync.FlushWindows(directory);
        else
            DirectorySync.FlushUnix(directory);
    }

    private static FileStream OpenExclusive(string path, FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateMode;
        var stream = new FileStream(path, options);
        try
        {
            MakePrivate(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void MakePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            MakePrivateWindows(path, inheritToChildren: true);
        else
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        RequirePrivate(path);
    }

    private static void MakePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
            MakePrivateWindows(path, inheritToChildren: false);
        else
            File.SetUnixFileMode(path, PrivateMode);
        RequirePrivate(path);
    }

    private static void RequirePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
            RequirePrivateWindows(path);
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException(NotPrivate);
    }

    [SupportedOSPlatform("windows")]
    private static void MakePrivateWindows(string path, bool inheritToChildren)
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new IOException(NotPrivate);
        var inherit = inheritToChildren
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var rule = new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow);
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(rule);
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(rule);
        new FileInfo(path).SetAccessControl(fileSecurity);
    }

    [SupportedOSPlatform("windows")]
    private static void RequirePrivateWindows(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new IOException(NotPrivate);
        var security = Directory.Exists(path)
            ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid && sid.Equals(current))
                continue;
            throw new IOException(NotPrivate);
        }
    }

    private static byte[] Encode(byte[] pkcs8)
    {
        var file = new byte[HeaderLength + pkcs8.Length + ChecksumLength];
        Magic.CopyTo(file);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(6), (uint)pkcs8.Length);
        pkcs8.CopyTo(file.AsSpan(HeaderLength));
        SHA256.HashData(file.AsSpan(0, HeaderLength + pkcs8.Length)).CopyTo(file.AsSpan(HeaderLength + pkcs8.Length));
        return file;
    }

    private static bool TryDecodeKey(byte[] file, out byte[] pkcs8)
    {
        pkcs8 = [];
        if (file.Length < HeaderLength + ChecksumLength || !file.AsSpan(0, 4).SequenceEqual(Magic))
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4)) != FormatVersion)
            return false;
        var keyLength = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(6));
        if (keyLength == 0 || keyLength > MaxKeyBytes || file.Length != HeaderLength + keyLength + ChecksumLength)
            return false;
        var prefix = HeaderLength + (int)keyLength;
        var checksum = SHA256.HashData(file.AsSpan(0, prefix));
        if (!CryptographicOperations.FixedTimeEquals(checksum, file.AsSpan(prefix, ChecksumLength)))
            return false;
        pkcs8 = file.AsSpan(HeaderLength, (int)keyLength).ToArray();
        return true;
    }

    private static DurablePrincipal Read(FileStream gate, byte[] file)
    {
        if (!TryDecodeKey(file, out var pkcs8))
            throw new IOException(InvalidFile);
        try
        {
            return Adopt(gate, pkcs8);
        }
        catch (CryptographicException)
        {
            throw new IOException(InvalidFile);
        }
    }

    private static DurablePrincipal Adopt(FileStream gate, byte[] pkcs8)
    {
        var key = LoadCanonicalKey(pkcs8);
        try
        {
            var publicKey = key.ExportSubjectPublicKeyInfo();
            var fingerprint = SHA256.HashData(publicKey);
            return new DurablePrincipal(gate, key, publicKey, fingerprint);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static ECDsa LoadCanonicalKey(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            ImportOwnedKey(key, pkcs8);
            if (!IsCanonicalP256(key.ExportSubjectPublicKeyInfo()))
                throw new CryptographicException("Public key encoding is not canonical.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string ToText(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static class DirectorySync
    {
        [UnsupportedOSPlatform("windows")]
        public static void FlushUnix(string directory)
        {
            var fd = Open(directory, 0);
            if (fd < 0)
                throw new IOException("Flush failed.");
            try
            {
                if (Sync(fd) != 0)
                    throw new IOException("Flush failed.");
            }
            finally
            {
                _ = Close(fd);
            }
        }

        [SupportedOSPlatform("windows")]
        public static void FlushWindows(string directory)
        {
            const uint readWrite = 0x80000000 | 0x40000000;
            const uint share = 0x1 | 0x2 | 0x4;
            const uint openExisting = 3;
            const uint backupSemantics = 0x02000000;
            using var handle = OpenDirectory(directory, readWrite, share, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
            if (handle.IsInvalid || !FlushBuffers(handle))
                throw new IOException("Flush failed.");
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static extern int Sync(int fd);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle OpenDirectory(
            string path,
            uint access,
            uint share,
            IntPtr security,
            uint disposition,
            uint flags,
            IntPtr template);

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
        private static extern bool FlushBuffers(SafeFileHandle handle);
    }
}
