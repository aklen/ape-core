using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Ape.Core.Aether;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Network.Services;
using Ape.Core.Network.Transport;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Ape.Core.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class AetherHandshakeTests
{
    [Fact]
    public void Authenticated_transfer_grants_delete_to_the_new_owner()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        var leftServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var rightServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var leftStore = Store();
        var rightStore = Store();
        using var leftLink = new AetherLabelLink(left.Network, new AetherHost(leftStore, 4096, 8),
            leftServices.GetRequiredService<ISceneManager>(), leftServices.GetRequiredService<ILogger>(),
            "entity-1", left.Principal.Id, "session-1", "catalog");
        using var rightLink = new AetherLabelLink(right.Network, new AetherHost(rightStore, 4096, 8),
            rightServices.GetRequiredService<ISceneManager>(), rightServices.GetRequiredService<ILogger>(),
            "entity-1", right.Principal.Id, "session-1", "catalog");
        Exchange(left, right);

        Assert.Equal(AetherAdmitKind.Queued, leftLink.SubmitCreate(TestStubEntity.RegistryTypeId).Kind);
        leftLink.Pump();
        right.Receive(left.Transport.Sent[0]);
        rightLink.Pump();

        Assert.Equal(AetherAdmitKind.Queued, leftLink.SubmitOwnershipTransfer(right.Principal.Id).Kind);
        leftLink.Pump();
        var transferPacket = left.Transport.Sent[1];
        Assert.True(AetherWire.TryDecode(transferPacket, "entity-1", "label", out var transfer, out _));
        Assert.True(AetherAuthority.Verify(transfer!));
        right.Receive(transferPacket);
        rightLink.Pump();

        Assert.Equal(right.Principal.Id, leftStore.GetOwner("entity-1"));
        Assert.Equal(right.Principal.Id, rightStore.GetOwner("entity-1"));
        Assert.Equal(AetherAdmitKind.Queued, rightLink.SubmitDelete().Kind);
        rightLink.Pump();
        var deletePacket = Assert.Single(right.Transport.Sent);
        left.Receive(deletePacket);
        leftLink.Pump();

        Assert.True(leftStore.IsDeleted("entity-1"));
        Assert.True(rightStore.IsDeleted("entity-1"));
    }

    [Fact]
    public void Authenticated_create_builds_the_registered_entity_on_the_remote_scene()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        var leftServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var rightServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var leftScene = leftServices.GetRequiredService<ISceneManager>();
        var rightScene = rightServices.GetRequiredService<ISceneManager>();
        var leftStore = Store();
        var rightStore = Store();
        var leftHost = new AetherHost(leftStore, 4096, 8);
        var rightHost = new AetherHost(rightStore, 4096, 8);
        using var leftLink = new AetherLabelLink(
            left.Network,
            leftHost,
            leftScene,
            leftServices.GetRequiredService<ILogger>(),
            "entity-1",
            left.Principal.Id,
            "session-1",
            "catalog");
        using var rightLink = new AetherLabelLink(
            right.Network,
            rightHost,
            rightScene,
            rightServices.GetRequiredService<ILogger>(),
            "entity-1",
            right.Principal.Id,
            "session-1",
            "catalog");
        Exchange(left, right);

        Assert.Equal(AetherAdmitKind.Queued, leftLink.SubmitCreate(TestStubEntity.RegistryTypeId).Kind);
        leftLink.Pump();
        var packet = Assert.Single(left.Transport.Sent);
        Assert.Equal(AetherWire.CreateType, packet[0]);
        right.Receive(packet);
        Assert.Equal(AetherAdmitKind.Queued, rightLink.LastRemoteAdmit!.Value.Kind);
        rightLink.Pump();

        var projected = Assert.IsType<TestStubEntity>(rightScene.GetEntity("entity-1"));
        Assert.Equal(left.Principal.Id, ((Ape.Core.Replication.Replica)projected).OwnerId);
        var stored = Assert.Single(rightStore.Entities);
        Assert.Equal(left.Principal.Id, stored.CreatorId);
        Assert.Equal(left.Principal.Id, stored.OwnerId);
        Assert.True(right.Network.TryGetPeer(right.Slot, out var peer));
        Assert.True(peer.IsAuthenticated);

        using var archiveDir = new TempDir();
        var archive = new AetherArchive(archiveDir.Path);
        archive.Save(leftStore);
        leftLink.Dispose();
        left.Transport.Sent.Clear();
        var restartedHost = new AetherHost(archive.Load(), 4096, 8);
        using var restartedLink = new AetherLabelLink(
            left.Network,
            restartedHost,
            leftScene,
            leftServices.GetRequiredService<ILogger>(),
            "entity-1",
            left.Principal.Id,
            "session-2",
            "catalog");
        restartedLink.CatchUp();
        Assert.Equal(packet, Assert.Single(left.Transport.Sent));
    }

    [Fact]
    public void Authenticated_owner_delete_waits_for_reordered_create_and_survives_restart()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var archiveDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        var leftServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var rightServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var leftScene = leftServices.GetRequiredService<ISceneManager>();
        var rightScene = rightServices.GetRequiredService<ISceneManager>();
        var leftStore = Store();
        var rightStore = Store();
        var leftHost = new AetherHost(leftStore, 4096, 8);
        var rightHost = new AetherHost(rightStore, 4096, 8);
        using var leftLink = new AetherLabelLink(
            left.Network,
            leftHost,
            leftScene,
            leftServices.GetRequiredService<ILogger>(),
            "entity-1",
            left.Principal.Id,
            "session-1",
            "catalog");
        using var rightLink = new AetherLabelLink(
            right.Network,
            rightHost,
            rightScene,
            rightServices.GetRequiredService<ILogger>(),
            "entity-1",
            right.Principal.Id,
            "session-1",
            "catalog");
        Exchange(left, right);

        Assert.Equal(AetherAdmitKind.Queued, leftLink.SubmitCreate(TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, leftLink.SubmitDelete().Kind);
        leftLink.Pump();
        var create = Assert.Single(left.Transport.Sent, packet => AetherWire.IsCreatePacket(packet));
        var delete = Assert.Single(left.Transport.Sent, packet => AetherWire.IsDeletePacket(packet));

        right.Receive(delete);
        Assert.Equal("Delete entity owner is unknown.", rightLink.LastRemoteFault);
        right.Receive(create);
        rightLink.Pump();
        Assert.NotNull(rightScene.GetEntity("entity-1"));
        Assert.False(rightStore.IsDeleted("entity-1"));
        rightLink.Pump();
        Assert.Null(rightScene.GetEntity("entity-1"));
        Assert.True(rightStore.IsDeleted("entity-1"));
        Assert.Equal(left.Principal.Id, Assert.Single(rightStore.Deletions).WriterId);

        var archive = new AetherArchive(archiveDir.Path);
        archive.Save(leftStore);
        leftLink.Dispose();
        left.Transport.Sent.Clear();
        using var restartedLink = new AetherLabelLink(
            left.Network,
            new AetherHost(archive.Load(), 4096, 8),
            leftScene,
            leftServices.GetRequiredService<ILogger>(),
            "entity-1",
            left.Principal.Id,
            "session-2",
            "catalog");
        restartedLink.CatchUp();
        Assert.Equal(create, left.Transport.Sent[0]);
        Assert.Equal(delete, left.Transport.Sent[1]);
    }

    [Fact]
    public void A_mutual_handshake_authenticates_both_peers()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        Exchange(left, right);

        AssertAuthenticated(left, left.Slot, right.Principal.Id);
        AssertAuthenticated(right, right.Slot, left.Principal.Id);
        Assert.Empty(left.Transport.Sent);
        Assert.Empty(right.Transport.Sent);
    }

    [Fact]
    public void Reconnect_and_restart_authenticate_the_same_principal()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var right = Side.Create(rightDir.Path, time);
        var left = Side.Create(leftDir.Path, time);
        var id = left.Principal.Id;
        Exchange(left, right);
        AssertAuthenticated(right, right.Slot, id);

        left.Disconnect();
        right.Disconnect();
        Exchange(left, right);
        AssertAuthenticated(left, left.Slot, right.Principal.Id);
        AssertAuthenticated(right, right.Slot, id);

        left.Dispose();
        right.Disconnect();
        using var restarted = Side.Create(leftDir.Path, time);
        Assert.Equal(id, restarted.Principal.Id);
        Exchange(restarted, right);
        AssertAuthenticated(restarted, restarted.Slot, right.Principal.Id);
        AssertAuthenticated(right, right.Slot, id);
    }

    [Fact]
    public void A_principal_claimed_with_another_key_is_refused()
    {
        var time = new ManualTime();
        using var hostDir = new TempDir();
        using var claimedDir = new TempDir();
        using var keyDir = new TempDir();
        using var host = Side.Create(hostDir.Path, time);
        using var claimed = Side.Create(claimedDir.Path, time);
        using var otherKey = Side.Create(keyDir.Path, time);
        host.Open();
        var hello = PrincipalHandshake.EncodeHello(
            claimed.Principal.FingerprintBytes,
            otherKey.Principal.PublicKey,
            RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength));
        host.Receive(hello);
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));
    }

    [Fact]
    public void A_replayed_handshake_is_refused()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        var first = Exchange(left, right);
        left.Disconnect();
        right.Disconnect();
        left.Open();
        left.Receive(first.RightHello);
        left.Receive(first.RightProof);
        Assert.False(left.Network.TryGetPeer(left.Slot, out _));
    }

    [Fact]
    public void A_changed_nonce_is_refused()
    {
        var time = new ManualTime();
        using var hostDir = new TempDir();
        using var peerDir = new TempDir();
        using var host = Side.Create(hostDir.Path, time);
        using var peer = Side.Create(peerDir.Path, time);
        host.Open();
        var hostHello = host.Take();
        Assert.True(PrincipalHandshake.TryRead(hostHello, out var hostMessage));
        var nonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
        var transcript = PrincipalHandshake.Transcript(
            peer.Principal.FingerprintBytes,
            hostMessage.Fingerprint,
            nonce,
            hostMessage.Nonce,
            peer.Principal.PublicKey,
            hostMessage.PublicKey);
        var proof = PrincipalHandshake.EncodeProof(peer.Principal.Sign(transcript));
        nonce[^1] ^= 0x01;
        host.Receive(PrincipalHandshake.EncodeHello(peer.Principal.FingerprintBytes, peer.Principal.PublicKey, nonce));
        host.Receive(proof);
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));
    }

    [Fact]
    public void A_reflected_handshake_is_refused()
    {
        var time = new ManualTime();
        using var dir = new TempDir();
        using var host = Side.Create(dir.Path, time);
        host.Open();
        var hello = host.Take();
        host.Receive(hello);
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));
    }

    [Fact]
    public void A_signature_from_another_connection_is_refused()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        var first = Exchange(left, right);
        left.Disconnect();
        right.Disconnect();
        left.Open();
        var nonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
        left.Receive(PrincipalHandshake.EncodeHello(right.Principal, nonce));
        left.Receive(first.RightProof);
        Assert.False(left.Network.TryGetPeer(left.Slot, out _));
    }

    [Fact]
    public void A_bad_truncated_or_oversized_handshake_is_refused()
    {
        var time = new ManualTime();
        using var hostDir = new TempDir();
        using var peerDir = new TempDir();
        using var host = Side.Create(hostDir.Path, time);
        using var peer = Side.Create(peerDir.Path, time);

        host.Open();
        var nonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
        host.Receive(PrincipalHandshake.EncodeHello(peer.Principal, nonce));
        host.Receive(PrincipalHandshake.EncodeProof(new byte[64]));
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));

        host.Open();
        host.Receive([PrincipalHandshake.Marker, PrincipalHandshake.Version, 1]);
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));

        host.Open();
        var oversized = new byte[PrincipalHandshake.MaxBytes + 1];
        oversized[0] = PrincipalHandshake.Marker;
        oversized[1] = PrincipalHandshake.Version;
        host.Receive(oversized);
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));
    }

    [Fact]
    public void An_expired_or_disconnected_handshake_is_refused()
    {
        var time = new ManualTime();
        using var hostDir = new TempDir();
        using var peerDir = new TempDir();
        using var host = Side.Create(hostDir.Path, time);
        using var peer = Side.Create(peerDir.Path, time);

        host.Open();
        var hostHello = host.Take();
        time.Now += PrincipalHandshake.Lifetime + TimeSpan.FromSeconds(1);
        host.Network.PollEvents();
        var lateNonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
        host.Receive(PrincipalHandshake.EncodeHello(peer.Principal, lateNonce));
        host.Receive(Proof(peer.Principal, hostHello, lateNonce));
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));

        time.Now += TimeSpan.FromSeconds(1);
        host.Open();
        var slot = host.Slot;
        var disconnectedHello = host.Take();
        var nonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
        host.Receive(PrincipalHandshake.EncodeHello(peer.Principal, nonce));
        host.Disconnect();
        host.Transport.DeliverSlot(slot, Proof(peer.Principal, disconnectedHello, nonce));
        Assert.False(host.Network.TryGetPeer(slot, out _));
    }

    [Fact]
    public void A_failed_handshake_does_not_change_the_reducer()
    {
        var time = new ManualTime();
        using var hostDir = new TempDir();
        using var claimedDir = new TempDir();
        using var keyDir = new TempDir();
        using var host = Side.Create(hostDir.Path, time);
        using var claimed = Side.Create(claimedDir.Path, time);
        using var otherKey = Side.Create(keyDir.Path, time);
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = services.GetRequiredService<ISceneManager>();
        var log = services.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(
            scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1"),
            log);
        var reducerHost = new AetherHost(store, 4096, 8);
        using var link = new AetherLabelLink(
            host.Network,
            reducerHost,
            scene,
            log,
            "entity-1",
            host.Principal.Id,
            "s1",
            "catalog");
        host.Open();
        var actor = AetherActorId.Format(claimed.Principal.Id, "s1");
        var packet = AetherWire.Encode(new AetherOperation(
            $"{actor}/1",
            "entity-1",
            claimed.Principal.Id,
            actor,
            1,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("kept") }));
        host.Transport.DeliverSlot(host.Slot, packet);
        var clock = store.Clock;
        var records = store.RecordCount;
        var pending = reducerHost.PendingCount;
        host.Receive(PrincipalHandshake.EncodeHello(
            claimed.Principal.FingerprintBytes,
            otherKey.Principal.PublicKey,
            RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength)));
        link.Pump();
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));
        Assert.Equal(clock, store.Clock);
        Assert.Equal(records, store.RecordCount);
        Assert.Equal(pending, reducerHost.PendingCount);
    }

    [Fact]
    public void One_principal_cannot_be_authenticated_on_two_connections()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);
        Exchange(left, right);
        var leftSlot = left.Slot;
        var rightSlot = right.Slot;
        Exchange(left, right);
        Assert.False(left.Network.TryGetPeer(left.Slot, out _));
        Assert.False(right.Network.TryGetPeer(right.Slot, out _));
        AssertAuthenticated(left, leftSlot, right.Principal.Id);
        AssertAuthenticated(right, rightSlot, left.Principal.Id);
    }

    [Fact]
    public void A_principal_file_is_private()
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsProfileIsOwnerOnly();
            return;
        }

        using var dir = new TempDir();
        using (DurablePrincipal.Open(dir.Path))
        {
        }

        var principal = Path.Combine(dir.Path, "principal");
        var gate = Path.Combine(dir.Path, "principal.lock");
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir.Path));
        Assert.Equal(privateMode, File.GetUnixFileMode(principal));
        Assert.Equal(privateMode, File.GetUnixFileMode(gate));

        File.SetUnixFileMode(principal, privateMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var leaked = Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
        Assert.Equal(DurablePrincipal.NotPrivate, leaked.Message);

        File.SetUnixFileMode(principal, privateMode);
        File.SetUnixFileMode(gate, privateMode | UnixFileMode.GroupRead);
        var leakedLock = Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
        Assert.Equal(DurablePrincipal.NotPrivate, leakedLock.Message);
    }

    [Fact]
    public void A_padded_public_key_is_not_a_second_principal()
    {
        var time = new ManualTime();
        using var hostDir = new TempDir();
        using var peerDir = new TempDir();
        using var host = Side.Create(hostDir.Path, time);
        using var peer = Side.Create(peerDir.Path, time);
        var canonical = peer.Principal.PublicKey;
        var paddedA = canonical.Concat(new byte[] { 0x01 }).ToArray();
        var paddedB = canonical.Concat(new byte[] { 0x02 }).ToArray();
        Assert.True(DurablePrincipal.IsCanonicalP256(canonical));
        Assert.False(DurablePrincipal.IsCanonicalP256(paddedA));
        Assert.False(DurablePrincipal.IsCanonicalP256(paddedB));
        Assert.NotEqual(SHA256.HashData(paddedA), SHA256.HashData(paddedB));

        using var owned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = owned!.ExportPkcs8PrivateKey();
        using var loaded = ECDsa.Create();
        DurablePrincipal.ImportOwnedKey(loaded, pkcs8);
        var trailing = pkcs8.Concat(new byte[] { 0x00 }).ToArray();
        using var rejected = ECDsa.Create();
        Assert.Throws<CryptographicException>(() => DurablePrincipal.ImportOwnedKey(rejected, trailing));

        host.Open();
        var hostHello = host.Take();
        Assert.True(PrincipalHandshake.TryRead(hostHello, out var hostMessage));
        var nonce = RandomNumberGenerator.GetBytes(PrincipalHandshake.NonceLength);
        var fingerprint = SHA256.HashData(paddedA);
        var transcript = PrincipalHandshake.Transcript(
            fingerprint,
            hostMessage.Fingerprint,
            nonce,
            hostMessage.Nonce,
            paddedA,
            hostMessage.PublicKey);
        Assert.True(DurablePrincipal.Verify(canonical, transcript, peer.Principal.Sign(transcript)));
        Assert.False(DurablePrincipal.Verify(paddedA, transcript, peer.Principal.Sign(transcript)));
        host.Receive(PrincipalHandshake.EncodeHello(fingerprint, paddedA, nonce));
        host.Receive(PrincipalHandshake.EncodeProof(peer.Principal.Sign(transcript)));
        Assert.False(host.Network.TryGetPeer(host.Slot, out _));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsProfileIsOwnerOnly()
    {
        using var dir = new TempDir();
        var parent = Path.Combine(dir.Path, "parent");
        Directory.CreateDirectory(parent);
        var inherited = new DirectoryInfo(parent).GetAccessControl();
        inherited.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(inherited);

        var profile = Path.Combine(parent, "profile");
        using (DurablePrincipal.Open(profile))
        {
        }

        AssertOwnerOnly(profile);
        AssertOwnerOnly(Path.Combine(profile, "principal"));
        AssertOwnerOnly(Path.Combine(profile, "principal.lock"));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnly(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user is missing.");
        var security = Directory.Exists(path)
            ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            Assert.Equal(current, rule.IdentityReference);
        }
    }

    private static void MakeOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            MakeOwnerOnlyFile(path);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static void MakeOwnerOnlyFile(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user is missing.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void ShareWithOthers(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ShareWithUsers(path);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    [SupportedOSPlatform("windows")]
    private static void ShareWithUsers(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static byte[] FrameKey(byte[] pkcs8)
    {
        var file = new byte[10 + pkcs8.Length + 32];
        "APR1"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(6), (uint)pkcs8.Length);
        pkcs8.CopyTo(file.AsSpan(10));
        SHA256.HashData(file.AsSpan(0, 10 + pkcs8.Length)).CopyTo(file.AsSpan(10 + pkcs8.Length));
        return file;
    }

    [Fact]
    public void A_missing_principal_keeps_a_valid_temporary_key()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        string id;
        using (var opened = DurablePrincipal.Open(dir.Path))
            id = opened.Id;
        var kept = File.ReadAllBytes(principal);
        File.Move(principal, temporary);

        using (var opened = DurablePrincipal.Open(dir.Path))
            Assert.Equal(id, opened.Id);

        Assert.Equal(kept, File.ReadAllBytes(principal));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void A_failed_rename_keeps_the_temporary_principal()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        try
        {
            DurablePrincipal.FailNextRenames(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
            Assert.False(File.Exists(principal));
            var kept = File.ReadAllBytes(temporary);

            DurablePrincipal.FailNextRenames(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
            Assert.Equal(kept, File.ReadAllBytes(temporary));
            Assert.False(File.Exists(principal));

            string id;
            using (var opened = DurablePrincipal.Open(dir.Path))
                id = opened.Id;
            Assert.Equal(kept, File.ReadAllBytes(principal));
            Assert.False(File.Exists(temporary));
            using (var again = DurablePrincipal.Open(dir.Path))
                Assert.Equal(id, again.Id);
        }
        finally
        {
            DurablePrincipal.ClearFaults();
        }
    }

    [Fact]
    public void A_failed_flush_does_not_replace_the_temporary_principal()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        try
        {
            DurablePrincipal.FailNextFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
            Assert.False(File.Exists(principal));
            var kept = File.ReadAllBytes(temporary);

            DurablePrincipal.FailNextFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
            Assert.Equal(kept, File.ReadAllBytes(temporary));
            Assert.False(File.Exists(principal));

            string id;
            using (var opened = DurablePrincipal.Open(dir.Path))
                id = opened.Id;
            Assert.Equal(kept, File.ReadAllBytes(principal));
            Assert.False(File.Exists(temporary));
            using (var again = DurablePrincipal.Open(dir.Path))
                Assert.Equal(id, again.Id);
        }
        finally
        {
            DurablePrincipal.ClearFaults();
        }
    }

    [Fact]
    public void A_failed_directory_flush_keeps_the_renamed_principal()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        try
        {
            DurablePrincipal.FailNextDirectoryFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
            Assert.False(File.Exists(temporary));
            var kept = File.ReadAllBytes(principal);

            DurablePrincipal.FailNextDirectoryFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
            Assert.Equal(kept, File.ReadAllBytes(principal));
            Assert.False(File.Exists(temporary));

            string id;
            using (var opened = DurablePrincipal.Open(dir.Path))
                id = opened.Id;
            Assert.Equal(kept, File.ReadAllBytes(principal));
            using (var again = DurablePrincipal.Open(dir.Path))
                Assert.Equal(id, again.Id);
        }
        finally
        {
            DurablePrincipal.ClearFaults();
        }
    }

    [Fact]
    public void A_missing_profile_parent_is_refused()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "missing", "profile");
        var rejected = Assert.Throws<IOException>(() => DurablePrincipal.Open(missing));
        Assert.Equal(DurablePrincipal.MissingParent, rejected.Message);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "missing")));
    }

    [Fact]
    public void A_failed_parent_flush_is_retried_before_the_principal_is_accepted()
    {
        using var dir = new TempDir();
        var profile = Path.Combine(dir.Path, "profile");
        var principal = Path.Combine(profile, "principal");
        try
        {
            DurablePrincipal.FailNextParentFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(profile));
            Assert.True(Directory.Exists(profile));
            Assert.False(File.Exists(principal));

            DurablePrincipal.FailNextParentFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(profile));
            Assert.False(File.Exists(principal));

            string id;
            using (var opened = DurablePrincipal.Open(profile))
                id = opened.Id;
            var kept = File.ReadAllBytes(principal);

            DurablePrincipal.FailNextParentFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(profile));
            Assert.Equal(kept, File.ReadAllBytes(principal));

            DurablePrincipal.FailNextParentFlushes(1);
            Assert.Throws<IOException>(() => DurablePrincipal.Open(profile));
            Assert.Equal(kept, File.ReadAllBytes(principal));

            using (var opened = DurablePrincipal.Open(profile))
                Assert.Equal(id, opened.Id);
        }
        finally
        {
            DurablePrincipal.ClearFaults();
        }
    }

    [Fact]
    public void A_torn_temporary_principal_is_replaced()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        File.WriteAllBytes(temporary, [1, 2, 3, 4]);
        MakeOwnerOnly(temporary);

        string id;
        using (var opened = DurablePrincipal.Open(dir.Path))
            id = opened.Id;
        Assert.False(File.Exists(temporary));
        using (var again = DurablePrincipal.Open(dir.Path))
            Assert.Equal(id, again.Id);
    }

    [Fact]
    public void A_shared_temporary_principal_is_refused()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        using (DurablePrincipal.Open(dir.Path))
        {
        }

        var kept = File.ReadAllBytes(principal);
        File.Move(principal, temporary);
        ShareWithOthers(temporary);
        var leaked = Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
        Assert.Equal(DurablePrincipal.NotPrivate, leaked.Message);
        Assert.Equal(kept, File.ReadAllBytes(temporary));
        Assert.False(File.Exists(principal));
    }

    [Fact]
    public void A_framed_non_p256_temporary_key_is_not_published()
    {
        using var dir = new TempDir();
        var principal = Path.Combine(dir.Path, "principal");
        var temporary = principal + ".tmp";
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var framed = FrameKey(other!.ExportPkcs8PrivateKey());
        File.WriteAllBytes(temporary, framed);
        MakeOwnerOnly(temporary);

        var rejected = Assert.Throws<IOException>(() => DurablePrincipal.Open(dir.Path));
        Assert.Equal(DurablePrincipal.InvalidFile, rejected.Message);
        Assert.Equal(framed, File.ReadAllBytes(temporary));
        Assert.False(File.Exists(principal));
    }

    [Fact]
    public void An_unauthenticated_claim_does_not_enter_the_reducer()
    {
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = services.GetRequiredService<ISceneManager>();
        var log = services.GetRequiredService<ILogger>();
        SceneCommitApplicator.Apply(
            scene,
            new CreateRegisteredEntityCommitRequest(TestStubEntity.RegistryTypeId, "entity-1"),
            log);
        var actor = AetherActorId.Format("peer-a", "s1");
        var packet = AetherWire.Encode(new AetherOperation(
            "peer-a/s1/1",
            "entity-1",
            "peer-a",
            actor,
            1,
            4,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("kept") }));

        var closed = new ScriptedTransport();
        var closedNetwork = new NetworkManager(closed, "peer-b");
        var closedHost = new AetherHost(store, 4096, 8);
        using var closedLink = new AetherLabelLink(closedNetwork, closedHost, scene, log, "entity-1", "peer-b", "s1", "catalog");
        var closedSlot = closed.OpenSlot();
        closed.DeliverSlot(closedSlot, packet);
        closed.DeliverSlot(closedSlot, PeerHello.Encode("peer-a"));
        closedLink.Pump();
        Assert.False(closedNetwork.TryGetPeer(closedSlot, out _));
        Assert.Equal(0, store.Clock);
        Assert.Equal(0, store.RecordCount);
        Assert.Equal(0, closedHost.PendingCount);

        var store2 = new AetherReducer();
        store2.DefineField("label", "lww");
        var open = new ScriptedTransport();
        var openNetwork = new NetworkManager(open, "peer-b", allowUnauthenticated: true);
        var openHost = new AetherHost(store2, 4096, 8);
        using var openLink = new AetherLabelLink(openNetwork, openHost, scene, log, "entity-1", "peer-b", "s1", "catalog");
        var openSlot = open.OpenSlot();
        open.DeliverSlot(openSlot, packet);
        open.DeliverSlot(openSlot, PeerHello.Encode("peer-a"));
        openLink.Pump();
        Assert.True(openNetwork.TryGetPeer(openSlot, out var peer));
        Assert.False(peer.IsAuthenticated);
        Assert.Equal("Peer is not authenticated.", openLink.LastRemoteFault);
        Assert.Equal(0, store2.Clock);
        Assert.Equal(0, store2.RecordCount);
        Assert.Equal(0, openHost.PendingCount);
    }

    [Fact]
    public void A_failed_send_retries_the_same_handshake_bytes()
    {
        var time = new ManualTime();
        using var leftDir = new TempDir();
        using var rightDir = new TempDir();
        using var left = Side.Create(leftDir.Path, time);
        using var right = Side.Create(rightDir.Path, time);

        left.Transport.FailNextSends = 1;
        left.Open();
        var rejectedHello = left.Transport.Rejected;
        Assert.Empty(left.Transport.Handshakes);
        left.Network.PollEvents();
        var hello = left.Take();
        Assert.Equal(rejectedHello, hello);
        left.Network.PollEvents();
        Assert.Single(left.Transport.Handshakes);

        right.Open();
        var rightHello = right.Take();
        right.Transport.FailNextSends = 1;
        right.Receive(hello);
        var rejectedProof = right.Transport.Rejected;
        Assert.Single(right.Transport.Handshakes);
        right.Network.PollEvents();
        var proof = right.Take();
        Assert.Equal(rejectedProof, proof);
        right.Network.PollEvents();
        Assert.Equal(2, right.Transport.Handshakes.Count);

        left.Receive(rightHello);
        left.Receive(proof);
        AssertAuthenticated(left, left.Slot, right.Principal.Id);
    }

    [Fact]
    public void Three_authenticated_peers_project_two_entities_once()
    {
        var time = new ManualTime();
        using var aDir = new TempDir();
        using var bDir = new TempDir();
        using var cDir = new TempDir();
        using var a = Side.Create(aDir.Path, time);
        using var b = Side.Create(bDir.Path, time);
        using var c = Side.Create(cDir.Path, time);
        var aServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var bServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var cServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var aScene = aServices.GetRequiredService<ISceneManager>();
        var bScene = bServices.GetRequiredService<ISceneManager>();
        var cScene = cServices.GetRequiredService<ISceneManager>();
        var aStore = SceneStore();
        var bStore = SceneStore();
        var cStore = SceneStore();
        using var aLink = SceneLink(a, aStore, aServices);
        using var bLink = SceneLink(b, bStore, bServices);
        using var cLink = SceneLink(c, cStore, cServices);

        Exchange(a, b);
        var bFromA = b.Slot;
        Exchange(a, c);
        var cFromA = c.Slot;
        Exchange(b, c);

        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitCreate("sphere", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitLabel("cube", "red").Kind);
        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitOffset("cube", 4).Kind);
        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitLabel("sphere", "blue").Kind);
        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitOffset("sphere", 9).Kind);
        var sent = a.Transport.Sent.Count;
        aLink.Pump();
        DeliverNew(a, sent, b, bFromA, c, cFromA);
        bLink.Pump();
        cLink.Pump();

        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitPublish("cube").Kind);
        Assert.Equal(AetherAdmitKind.Queued, aLink.SubmitPublish("sphere").Kind);
        sent = a.Transport.Sent.Count;
        aLink.Pump();
        DeliverNew(a, sent, b, bFromA, c, cFromA);
        bLink.Pump();
        cLink.Pump();

        AssertScene(aScene, aStore, a.Principal.Id);
        AssertScene(bScene, bStore, a.Principal.Id);
        AssertScene(cScene, cStore, a.Principal.Id);
        var cube = bScene.GetEntity("cube");
        var sphere = bScene.GetEntity("sphere");
        bLink.Pump();
        Assert.Same(cube, bScene.GetEntity("cube"));
        Assert.Same(sphere, bScene.GetEntity("sphere"));

        ((SceneManager)bScene).DeliverReplica(new Ape.Core.Event.Models.ReplicaReceivedEvent
        {
            ReplicaId = "cube",
            OwnerId = "intruder",
            TypeName = typeof(TestStubEntity).AssemblyQualifiedName!,
            Payload = [1],
            SenderId = "replica",
        });
        Assert.Same(cube, bScene.GetEntity("cube"));
        Assert.Equal(a.Principal.Id, Assert.IsType<TestStubEntity>(cube).OwnerId);

        b.Transport.DisconnectSlot(bFromA);
        Assert.Same(cube, bScene.GetEntity("cube"));
        Assert.Same(sphere, bScene.GetEntity("sphere"));
        Assert.Equal("red", Assert.IsType<TestStubEntity>(bScene.GetEntity("cube")).Label);
        Assert.Equal(9, Assert.IsType<TestStubEntity>(cScene.GetEntity("sphere")).Offset);

        var again = cScene.GetEntity("cube");
        var catchUp = a.Transport.Sent.Count;
        aLink.CatchUp();
        aLink.Pump();
        DeliverNew(a, catchUp, b, bFromA, c, cFromA);
        cLink.Pump();
        Assert.Same(again, cScene.GetEntity("cube"));
        Assert.Equal("blue", Assert.IsType<TestStubEntity>(cScene.GetEntity("sphere")).Label);
    }

    [Fact]
    public void Publication_and_hide_gate_the_shared_fields()
    {
        var time = new ManualTime();
        using var ownerDir = new TempDir();
        using var otherDir = new TempDir();
        using var owner = Side.Create(ownerDir.Path, time);
        using var other = Side.Create(otherDir.Path, time);
        var ownerServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var otherServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var ownerScene = ownerServices.GetRequiredService<ISceneManager>();
        var otherScene = otherServices.GetRequiredService<ISceneManager>();
        var ownerStore = SceneStore();
        var otherStore = SceneStore();
        using var ownerLink = SceneLink(owner, ownerStore, ownerServices);
        using var otherLink = SceneLink(other, otherStore, otherServices);
        Exchange(owner, other);
        var otherFromOwner = other.Slot;

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("sphere", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitLabel("cube", "red").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitOffset("cube", 4).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitLabel("sphere", "blue").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitOffset("sphere", 9).Kind);
        var sent = owner.Transport.Sent.Count;
        ownerLink.Pump();
        DeliverOne(owner, sent, other, otherFromOwner);
        otherLink.Pump();

        var cube = Assert.IsType<TestStubEntity>(otherScene.GetEntity("cube"));
        var sphere = Assert.IsType<TestStubEntity>(otherScene.GetEntity("sphere"));
        Assert.Null(cube.Label);
        Assert.Equal(0, cube.Offset);
        Assert.Null(sphere.Label);
        Assert.Equal(0, sphere.Offset);

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitPublish("cube").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitPublish("sphere").Kind);
        sent = owner.Transport.Sent.Count;
        ownerLink.Pump();
        DeliverOne(owner, sent, other, otherFromOwner);
        otherLink.Pump();
        Assert.Equal("red", cube.Label);
        Assert.Equal(4, cube.Offset);
        Assert.Equal("blue", sphere.Label);
        Assert.Equal(9, sphere.Offset);
        Assert.Equal(owner.Principal.Id, otherStore.GetOwner("cube"));

        Assert.Equal(AetherAdmitKind.Refused, otherLink.SubmitPublish("cube").Kind);
        Assert.Equal(AetherAdmitKind.Refused, otherLink.SubmitHide("cube").Kind);
        var clock = otherStore.Clock;
        otherLink.Pump();
        Assert.Equal(clock, otherStore.Clock);
        Assert.True(otherStore.IsMember("cube", "catalog"));
        Assert.True(otherStore.IsSharedVisible("cube"));

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitHide("cube").Kind);
        sent = owner.Transport.Sent.Count;
        ownerLink.Pump();
        DeliverOne(owner, sent, other, otherFromOwner);
        otherLink.Pump();
        Assert.Same(cube, otherScene.GetEntity("cube"));
        Assert.Null(cube.Label);
        Assert.Equal(0, cube.Offset);
        Assert.Equal("blue", sphere.Label);
        Assert.Equal(9, sphere.Offset);
        Assert.False(otherStore.IsSharedVisible("cube"));
        Assert.Equal(4, otherStore.RawContribution("cube", owner.Principal.Id, "offset"));

        other.Transport.DisconnectSlot(otherFromOwner);
        Assert.Same(cube, otherScene.GetEntity("cube"));
        Assert.Same(sphere, otherScene.GetEntity("sphere"));
    }

    [Fact]
    public void Restart_resends_the_signed_publication_and_hide()
    {
        var time = new ManualTime();
        using var ownerDir = new TempDir();
        using var otherDir = new TempDir();
        using var archiveDir = new TempDir();
        using var owner = Side.Create(ownerDir.Path, time);
        using var other = Side.Create(otherDir.Path, time);
        var ownerServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var otherServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var otherScene = otherServices.GetRequiredService<ISceneManager>();
        var ownerStore = SceneStore();
        var otherStore = SceneStore();
        using var ownerLink = SceneLink(owner, ownerStore, ownerServices);
        using var otherLink = SceneLink(other, otherStore, otherServices);
        Exchange(owner, other);
        var otherFromOwner = other.Slot;

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitLabel("cube", "red").Kind);
        var created = owner.Transport.Sent.Count;
        ownerLink.Pump();
        var createPacket = owner.Transport.Sent.Skip(created).First(packet => packet[0] == AetherWire.CreateType);
        DeliverOne(owner, created, other, otherFromOwner);
        otherLink.Pump();

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitPublish("cube").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitHide("cube").Kind);
        var lifecycle = owner.Transport.Sent.Count;
        ownerLink.Pump();
        var publish = owner.Transport.Sent.Skip(lifecycle).First(packet => packet[0] == AetherWire.PublishType);
        var hide = owner.Transport.Sent.Skip(lifecycle).First(packet => packet[0] == AetherWire.HideType);

        var archive = new AetherArchive(archiveDir.Path);
        archive.Save(ownerStore);
        ownerLink.Dispose();
        owner.Transport.Sent.Clear();
        using var restarted = new AetherSceneLink(
            owner.Network,
            new AetherHost(archive.Load(), 65_536, 64),
            ownerServices.GetRequiredService<ISceneManager>(),
            ownerServices.GetRequiredService<ILogger>(),
            owner.Principal.Id,
            "session-2");
        restarted.CatchUp();
        restarted.Pump();

        Assert.Contains(owner.Transport.Sent, packet => packet.AsSpan().SequenceEqual(createPacket));
        Assert.Contains(owner.Transport.Sent, packet => packet.AsSpan().SequenceEqual(publish));
        Assert.Contains(owner.Transport.Sent, packet => packet.AsSpan().SequenceEqual(hide));
        foreach (var packet in owner.Transport.Sent)
            other.Transport.DeliverSlot(otherFromOwner, packet);
        otherLink.Pump();

        var cube = Assert.IsType<TestStubEntity>(otherScene.GetEntity("cube"));
        Assert.Null(cube.Label);
        Assert.True(otherStore.IsMember("cube", "catalog"));
        Assert.False(otherStore.IsSharedVisible("cube"));
        Assert.Equal("red", otherStore.ResolveLww("cube", "label")?.Text);
    }

    [Fact]
    public void Interest_sends_one_entity_and_leaves_membership_unchanged()
    {
        var listed = AetherWire.EncodeInterest(["cube", "sphere"]);
        Assert.True(AetherWire.TryDecodeInterest(listed, out var decoded, out var decodedFault));
        Assert.Null(decodedFault);
        Assert.Equal(["cube", "sphere"], decoded);
        Assert.True(AetherWire.TryDecodeInterest(AetherWire.EncodeInterest([]), out var empty, out var emptyFault));
        Assert.Null(emptyFault);
        Assert.Empty(empty);
        Assert.Throws<AetherProtocolException>(() => AetherWire.EncodeInterest(["cube", "cube"]));
        Assert.Throws<AetherProtocolException>(() => AetherWire.EncodeInterest([""]));
        Assert.Throws<AetherProtocolException>(() => AetherWire.EncodeInterest(
            Enumerable.Range(0, AetherWire.MaxInterestCount + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));

        var time = new ManualTime();
        using var ownerDir = new TempDir();
        using var interestedDir = new TempDir();
        using var openDir = new TempDir();
        using var owner = Side.Create(ownerDir.Path, time);
        using var interested = Side.Create(interestedDir.Path, time);
        using var open = Side.Create(openDir.Path, time);
        var ownerServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var interestedServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var openServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var ownerScene = ownerServices.GetRequiredService<ISceneManager>();
        var interestedScene = interestedServices.GetRequiredService<ISceneManager>();
        var openScene = openServices.GetRequiredService<ISceneManager>();
        var ownerStore = SceneStore();
        var interestedStore = SceneStore();
        var openStore = SceneStore();
        using var ownerLink = SceneLink(owner, ownerStore, ownerServices);
        using var interestedLink = SceneLink(interested, interestedStore, interestedServices);
        using var openLink = SceneLink(open, openStore, openServices);

        var ownerInterested = Exchange(owner, interested);
        var ownerToInterested = ownerInterested.LeftSlot;
        var interestedFromOwner = interested.Slot;
        var ownerOpen = Exchange(owner, open);
        var ownerToOpen = ownerOpen.LeftSlot;
        var openFromOwner = open.Slot;
        Exchange(interested, open);

        var declared = interested.Transport.Directed.Count;
        Assert.True(interestedLink.DeclareInterest(["cube"]));
        interestedLink.Pump();
        var interest = interested.Transport.Directed
            .Skip(declared)
            .First(item => item.PeerId == interestedFromOwner && AetherWire.IsInterestPacket(item.Data))
            .Data;
        owner.Transport.DeliverSlot(ownerToInterested, interest);
        Assert.Null(ownerLink.LastRemoteFault);
        Assert.Equal(0, ownerStore.Clock);
        Assert.Null(ownerStore.GetEntity("cube"));

        var damaged = interest.ToArray();
        damaged[^1] ^= 0xFF;
        owner.Transport.DeliverSlot(ownerToInterested, damaged);
        Assert.Equal("State packet checksum does not match.", ownerLink.LastRemoteFault);
        Assert.Equal(0, ownerStore.Clock);

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("sphere", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitLabel("cube", "red").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitOffset("cube", 4).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitLabel("sphere", "blue").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitOffset("sphere", 9).Kind);
        var sent = owner.Transport.Directed.Count;
        ownerLink.Pump();
        Assert.NotEmpty(PacketsFor(owner, sent, ownerToInterested));
        Assert.All(PacketsFor(owner, sent, ownerToInterested), packet => Assert.Equal("cube", EntityOf(packet)));
        Assert.Contains(PacketsFor(owner, sent, ownerToOpen), packet => EntityOf(packet) == "sphere");
        DeliverDirected(owner, sent, ownerToInterested, interested, interestedFromOwner);
        DeliverDirected(owner, sent, ownerToOpen, open, openFromOwner);
        interestedLink.Pump();
        openLink.Pump();

        var cube = Assert.IsType<TestStubEntity>(interestedScene.GetEntity("cube"));
        Assert.Null(interestedScene.GetEntity("sphere"));
        Assert.Null(cube.Label);
        Assert.Equal(0, cube.Offset);
        Assert.False(interestedStore.IsMember("cube", "catalog"));
        Assert.IsType<TestStubEntity>(openScene.GetEntity("sphere"));
        Assert.Equal(AetherAdmitKind.Refused, interestedLink.SubmitHide("cube").Kind);
        Assert.Equal(AetherAdmitKind.Refused, interestedLink.SubmitPublish("cube").Kind);
        Assert.True(ownerStore.IsSharedVisible("cube"));

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitPublish("cube").Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitPublish("sphere").Kind);
        sent = owner.Transport.Directed.Count;
        ownerLink.Pump();
        Assert.All(PacketsFor(owner, sent, ownerToInterested), packet => Assert.Equal("cube", EntityOf(packet)));
        Assert.Contains(PacketsFor(owner, sent, ownerToOpen), packet => EntityOf(packet) == "sphere");
        DeliverDirected(owner, sent, ownerToInterested, interested, interestedFromOwner);
        DeliverDirected(owner, sent, ownerToOpen, open, openFromOwner);
        interestedLink.Pump();
        openLink.Pump();

        Assert.Equal("red", cube.Label);
        Assert.Equal(4, cube.Offset);
        Assert.Null(interestedScene.GetEntity("sphere"));
        Assert.True(interestedStore.IsMember("cube", "catalog"));
        Assert.False(interestedStore.IsMember("sphere", "catalog"));
        Assert.Equal("blue", Assert.IsType<TestStubEntity>(openScene.GetEntity("sphere")).Label);
        Assert.Equal(9, Assert.IsType<TestStubEntity>(openScene.GetEntity("sphere")).Offset);

        owner.Transport.DisconnectSlot(ownerToInterested);
        interested.Transport.DisconnectSlot(interestedFromOwner);
        var again = Exchange(owner, interested);
        var ownerToInterestedAgain = again.LeftSlot;
        var interestedFromOwnerAgain = interested.Slot;
        sent = owner.Transport.Directed.Count;
        ownerLink.Pump();
        Assert.Contains(PacketsFor(owner, sent, ownerToInterestedAgain), packet => EntityOf(packet) == "sphere");
        DeliverDirected(owner, sent, ownerToInterestedAgain, interested, interestedFromOwnerAgain);
        interestedLink.Pump();
        interestedLink.Pump();
        var sphere = Assert.IsType<TestStubEntity>(interestedScene.GetEntity("sphere"));
        Assert.Equal("blue", sphere.Label);
        Assert.Equal(9, sphere.Offset);
        Assert.Same(cube, interestedScene.GetEntity("cube"));
    }

    [Fact]
    public void An_empty_interest_receives_nothing()
    {
        var time = new ManualTime();
        using var ownerDir = new TempDir();
        using var quietDir = new TempDir();
        using var owner = Side.Create(ownerDir.Path, time);
        using var quiet = Side.Create(quietDir.Path, time);
        var ownerServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var quietServices = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var ownerStore = SceneStore();
        var quietStore = SceneStore();
        using var ownerLink = SceneLink(owner, ownerStore, ownerServices);
        using var quietLink = SceneLink(quiet, quietStore, quietServices);
        var link = Exchange(owner, quiet);
        var ownerToQuiet = link.LeftSlot;
        var quietFromOwner = quiet.Slot;

        var declared = quiet.Transport.Directed.Count;
        Assert.True(quietLink.DeclareInterest([]));
        quietLink.Pump();
        var interest = quiet.Transport.Directed
            .Skip(declared)
            .First(item => item.PeerId == quietFromOwner && AetherWire.IsInterestPacket(item.Data))
            .Data;
        owner.Transport.DeliverSlot(ownerToQuiet, interest);

        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, ownerLink.SubmitCreate("sphere", TestStubEntity.RegistryTypeId).Kind);
        var sent = owner.Transport.Directed.Count;
        ownerLink.Pump();

        Assert.Empty(PacketsFor(owner, sent, ownerToQuiet));
        Assert.IsType<TestStubEntity>(ownerServices.GetRequiredService<ISceneManager>().GetEntity("cube"));
        Assert.Null(quietServices.GetRequiredService<ISceneManager>().GetEntity("cube"));
        Assert.False(quietStore.IsMember("cube", "catalog"));
    }

    [Fact]
    public void A_replica_update_or_delete_leaves_a_projected_entity()
    {
        var time = new ManualTime();
        using var dir = new TempDir();
        using var side = Side.Create(dir.Path, time);
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = services.GetRequiredService<ISceneManager>();
        var replicas = services.GetRequiredService<IReplicaManager>();
        var store = SceneStore();
        using var link = SceneLink(side, store, services);

        Assert.Equal(AetherAdmitKind.Queued, link.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Queued, link.SubmitLabel("cube", "red").Kind);
        Assert.Equal(AetherAdmitKind.Queued, link.SubmitOffset("cube", 4).Kind);
        link.Pump();
        Assert.Equal(AetherAdmitKind.Queued, link.SubmitPublish("cube").Kind);
        link.Pump();

        var cube = Assert.IsType<TestStubEntity>(scene.GetEntity("cube"));
        Assert.Equal("red", cube.Label);
        Assert.Equal(4, cube.Offset);
        Assert.Same(cube, replicas.GetReplica("cube"));

        var incoming = new TestStubEntity { Id = "cube", Counter = 99, Label = "stolen", Offset = 50 };
        replicas.OnNetworkData("intruder", ReplicaPacketBuilder.BuildPacket(ReplicaPacketType.Update, incoming));
        replicas.OnNetworkData("intruder", ReplicaPacketBuilder.BuildPacket(ReplicaPacketType.FullSync, incoming));
        replicas.OnNetworkData("intruder", ReplicaPacketBuilder.BuildDeletePacket("cube"));

        Assert.Same(cube, scene.GetEntity("cube"));
        Assert.Same(cube, replicas.GetReplica("cube"));
        Assert.Equal("red", cube.Label);
        Assert.Equal(4, cube.Offset);
        Assert.Equal(0, cube.Counter);
        Assert.Equal(side.Principal.Id, cube.OwnerId);
        Assert.False(store.IsDeleted("cube"));

        TestStubEntity plain;
        using (SceneMutationScope.Begin())
            plain = Assert.IsType<TestStubEntity>(scene.CreateRegisteredEntity(TestStubEntity.RegistryTypeId, "plain", side.Principal.Id));
        plain.Counter = 1;
        var changed = new TestStubEntity { Id = "plain", Counter = 5 };
        replicas.OnNetworkData("intruder", ReplicaPacketBuilder.BuildPacket(ReplicaPacketType.Update, changed));
        Assert.Equal(5, plain.Counter);
        replicas.OnNetworkData("intruder", ReplicaPacketBuilder.BuildDeletePacket("plain"));
        Assert.Null(scene.GetEntity("plain"));
        Assert.Null(replicas.GetReplica("plain"));
        Assert.Same(cube, scene.GetEntity("cube"));
    }

    [Fact]
    public void A_full_scene_queue_does_not_create_the_second_entity()
    {
        var time = new ManualTime();
        using var dir = new TempDir();
        using var side = Side.Create(dir.Path, time);
        var services = RegisteredEntityCommitTests.BuildWithStubRegistered();
        var scene = services.GetRequiredService<ISceneManager>();
        var store = SceneStore();
        using var link = new AetherSceneLink(
            side.Network,
            new AetherHost(store, 4096, 1),
            scene,
            services.GetRequiredService<ILogger>(),
            side.Principal.Id,
            "session-1");

        Assert.Equal(AetherAdmitKind.Queued, link.SubmitCreate("cube", TestStubEntity.RegistryTypeId).Kind);
        Assert.Equal(AetherAdmitKind.Full, link.SubmitCreate("sphere", TestStubEntity.RegistryTypeId).Kind);
        link.Pump();

        Assert.IsType<TestStubEntity>(scene.GetEntity("cube"));
        Assert.Null(scene.GetEntity("sphere"));
        Assert.Null(store.GetEntity("sphere"));
    }

    private static IEnumerable<byte[]> PacketsFor(Side sender, int directedBefore, string slot)
    {
        foreach (var (peerId, data) in sender.Transport.Directed.Skip(directedBefore))
        {
            if (string.Equals(peerId, slot, StringComparison.Ordinal))
                yield return data;
        }
    }

    private static string EntityOf(byte[] packet)
    {
        if (AetherWire.IsContributionPacket(packet))
        {
            Assert.True(AetherWire.TryDecodeContributions(packet, out var fields, out _));
            return fields[0].Key.EntityId;
        }

        Assert.True(AetherWire.TryDecodeAny(packet, out var operation, out _));
        return operation!.EntityId;
    }

    private static void DeliverDirected(Side sender, int directedBefore, string senderSlot, Side receiver, string receiverSlot)
    {
        var seen = new List<byte[]>();
        foreach (var packet in PacketsFor(sender, directedBefore, senderSlot))
        {
            if (seen.Any(existing => existing.AsSpan().SequenceEqual(packet)))
                continue;
            seen.Add(packet);
            receiver.Transport.DeliverSlot(receiverSlot, packet);
        }
    }

    private static void DeliverOne(Side sender, int sentBefore, Side receiver, string slot)
    {
        var seen = new List<byte[]>();
        foreach (var packet in sender.Transport.Sent.Skip(sentBefore))
        {
            if (seen.Any(existing => existing.AsSpan().SequenceEqual(packet)))
                continue;
            seen.Add(packet);
            receiver.Transport.DeliverSlot(slot, packet);
        }
    }

    private static void DeliverNew(Side sender, int sentBefore, Side left, string leftSlot, Side right, string rightSlot)
    {
        var seen = new List<byte[]>();
        foreach (var packet in sender.Transport.Sent.Skip(sentBefore))
        {
            if (seen.Any(existing => existing.AsSpan().SequenceEqual(packet)))
                continue;
            seen.Add(packet);
            left.Transport.DeliverSlot(leftSlot, packet);
            right.Transport.DeliverSlot(rightSlot, packet);
        }
    }

    private static void AssertScene(ISceneManager scene, AetherReducer store, string principal)
    {
        var cube = Assert.IsType<TestStubEntity>(scene.GetEntity("cube"));
        var sphere = Assert.IsType<TestStubEntity>(scene.GetEntity("sphere"));
        Assert.Equal("red", cube.Label);
        Assert.Equal(4, cube.Offset);
        Assert.Equal("blue", sphere.Label);
        Assert.Equal(9, sphere.Offset);
        Assert.Equal(principal, cube.OwnerId);
        Assert.Equal(principal, sphere.OwnerId);
        Assert.Equal(principal, store.GetEntity("cube")!.CreatorId);
        Assert.Equal(principal, store.GetEntity("sphere")!.CreatorId);
        Assert.Equal(principal, store.GetOwner("cube"));
        Assert.Equal(principal, store.GetOwner("sphere"));
        Assert.Null(scene.GetEntity("extra"));
    }

    private static AetherSceneLink SceneLink(Side side, AetherReducer store, IServiceProvider services) =>
        new(
            side.Network,
            new AetherHost(store, 65_536, 64),
            services.GetRequiredService<ISceneManager>(),
            services.GetRequiredService<ILogger>(),
            side.Principal.Id,
            "session-1");

    private static AetherReducer SceneStore()
    {
        var reducer = Store();
        reducer.DefineField("offset", "sumContributions");
        return reducer;
    }

    private static Completed Exchange(Side left, Side right)
    {
        left.Open();
        right.Open();
        var leftHello = left.Take();
        var rightHello = right.Take();
        var leftSlot = left.Slot;
        right.Receive(leftHello);
        left.Receive(rightHello);
        var rightProof = right.Take();
        var leftProof = left.Take();
        left.Receive(rightProof);
        right.Receive(leftProof);
        return new Completed(leftSlot, leftHello, rightHello, leftProof, rightProof);
    }

    private static AetherReducer Store()
    {
        var reducer = new AetherReducer();
        reducer.DefineField("label", "lww");
        return reducer;
    }

    private static byte[] Proof(DurablePrincipal signer, byte[] peerHello, byte[] signerNonce)
    {
        Assert.True(PrincipalHandshake.TryRead(peerHello, out var peer));
        var transcript = PrincipalHandshake.Transcript(
            signer.FingerprintBytes,
            peer.Fingerprint,
            signerNonce,
            peer.Nonce,
            signer.PublicKey,
            peer.PublicKey);
        return PrincipalHandshake.EncodeProof(signer.Sign(transcript));
    }

    private static void AssertAuthenticated(Side side, string slot, string principal)
    {
        Assert.True(side.Network.TryGetPeer(slot, out var peer));
        Assert.Equal(principal, peer.PrincipalId);
        Assert.True(peer.IsAuthenticated);
        Assert.Equal(slot, peer.ConnectionId);
    }

    private readonly record struct Completed(
        string LeftSlot,
        byte[] LeftHello,
        byte[] RightHello,
        byte[] LeftProof,
        byte[] RightProof);

    private sealed class Side : IDisposable
    {
        private int _cursor;
        private bool _disposed;

        public required DurablePrincipal Principal { get; init; }

        public required ScriptedTransport Transport { get; init; }

        public required NetworkManager Network { get; init; }

        public string Slot { get; private set; } = "";

        public static Side Create(string directory, TimeProvider time)
        {
            var principal = DurablePrincipal.Open(directory);
            var transport = new ScriptedTransport();
            var network = new NetworkManager(transport, principal, time);
            return new Side { Principal = principal, Transport = transport, Network = network };
        }

        public void Open() => Slot = Transport.OpenSlot();

        public byte[] Take()
        {
            var packet = Transport.Handshakes[_cursor];
            _cursor++;
            return packet;
        }

        public void Receive(byte[] packet) => Transport.DeliverSlot(Slot, packet);

        public void Disconnect() => Transport.DisconnectSlot(Slot);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Network.Dispose();
            Principal.Dispose();
            Transport.Dispose();
        }
    }

    private sealed class ScriptedTransport : INetworkTransport
    {
        private int _nextSlot = 1;
        private readonly List<string> _peers = [];

        public string TransportType => "scripted";

        public string LocalPeerId => "scripted";

        public List<byte[]> Sent { get; } = [];

        public List<(string PeerId, byte[] Data)> Directed { get; } = [];

        public List<byte[]> Handshakes { get; } = [];

        public int FailNextSends { get; set; }

        public byte[]? Rejected { get; private set; }

        public event Action<string>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[], int>? OnDataReceived;
#pragma warning disable CS0067
        public event Action<string, Guid, int, byte[]>? OnFileChunkReceived;
        public event Action<string, Guid, int, long, string[]>? OnFileManifestReceived;
        public event Action<string, Guid, int>? OnFileChunkRequested;
#pragma warning restore CS0067

        public string OpenSlot()
        {
            var slot = _nextSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _nextSlot++;
            _peers.Add(slot);
            OnPeerConnected?.Invoke(slot);
            return slot;
        }

        public void DisconnectSlot(string slot)
        {
            _peers.Remove(slot);
            OnPeerDisconnected?.Invoke(slot);
        }

        public void DeliverSlot(string slot, byte[] data) => OnDataReceived?.Invoke(slot, data, data.Length);

        public void SendReliable(string peerId, byte[] data) => Take(peerId, data);

        public bool TrySendBulk(string peerId, byte[] data)
        {
            Take(peerId, data);
            return true;
        }

        public void SendUnreliable(string peerId, byte[] data) => Take(peerId, data);

        public IEnumerable<string> GetConnectedPeers() => _peers;

        public void StartServer(int port, string? peerName = null, string? role = null) { }

        public void Connect(string host, int port, string? peerName = null, string? role = null) { }

        public void PollEvents() { }

        public void Stop() { }

        public void SendFileChunk(string peerId, Guid fileId, int chunkIndex, byte[] data) { }

        public void RequestFileManifest(string peerId, Guid fileId) { }

        public void RequestFileChunk(string peerId, Guid fileId, int chunkIndex) { }

        public void Dispose() { }

        private void Take(string peerId, byte[] data)
        {
            if (FailNextSends > 0)
            {
                FailNextSends--;
                Rejected = data.ToArray();
                throw new IOException("Send failed.");
            }

            var copy = data.ToArray();
            if (copy.Length > 0 && copy[0] == PrincipalHandshake.Marker)
                Handshakes.Add(copy);
            else
                Sent.Add(copy);
            Directed.Add((peerId, copy));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ape-aether-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
