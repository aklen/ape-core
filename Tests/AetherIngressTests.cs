using Ape.Core.Aether;

namespace Ape.Core.Tests;

public sealed class AetherIngressTests
{
    [Fact]
    public void An_actor_is_bound_by_length_not_by_prefix()
    {
        var narrow = AetherActorId.Format("a", "b2:s1");
        var wide = AetherActorId.Format("ab", "s1");
        Assert.Equal("1:a5:b2:s1", narrow);
        Assert.True(AetherActorId.TryParse(narrow, out var parsed));
        Assert.Equal("a", parsed.PeerId);
        Assert.Equal("b2:s1", parsed.SessionId);
        Assert.True(AetherActorId.TryParse(wide, out var other));
        Assert.Equal("ab", other.PeerId);
        Assert.NotEqual(parsed.PeerId, other.PeerId);
        Assert.False(AetherActorId.TryParse("a/s1", out _));
        Assert.False(AetherActorId.TryParse(narrow + "x", out _));
    }

    [Fact]
    public void A_peers_own_writer_and_session_is_admitted()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var admit = host.TryAcceptRemote(Write("peer-a", "s1", 1, 10, "kept"), "peer-a");

        Assert.Equal(AetherAdmitKind.Queued, admit.Kind);
        Assert.Equal(1, admit.RequestId);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);
        Assert.Equal("kept", store.ResolveLww("entity-1", "label")?.Text);
        var cursor = Assert.Single(store.ActorCursors);
        Assert.Equal(AetherActorId.Format("peer-a", "s1"), cursor.ActorId);
        Assert.Equal(1, cursor.Sequence);
        Assert.Equal(10, cursor.Lamport);
    }

    [Fact]
    public void A_claimed_writer_from_another_connection_is_refused()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var admit = host.TryAcceptRemote(Write("peer-b", "s1", 1, 10, "stolen"), "peer-a");

        Assert.Equal(AetherAdmitKind.Refused, admit.Kind);
        Assert.Equal("Actor is not on this connection.", admit.Reason);
        Assert.Equal(0, admit.RequestId);
        AssertUnchanged(store, host);

        var own = host.TryAcceptRemote(Write("peer-a", "s1", 1, 10, "kept"), "peer-a");
        Assert.Equal(1, own.RequestId);
    }

    [Fact]
    public void Another_peers_actor_on_this_connection_is_refused()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var claimed = new AetherOperation(
            "claimed",
            "entity-1",
            "peer-a",
            AetherActorId.Format("peer-b", "s1"),
            1,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("stolen") });
        var prefixed = new AetherOperation(
            "prefixed",
            "entity-1",
            "a",
            AetherActorId.Format("ab", "s1"),
            1,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("stolen") });
        var raw = new AetherOperation(
            "raw",
            "entity-1",
            "peer-a",
            "peer-a/s1",
            1,
            10,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label("stolen") });

        Assert.Equal(AetherAdmitKind.Refused, host.TryAcceptRemote(claimed, "peer-a").Kind);
        Assert.Equal(AetherAdmitKind.Refused, host.TryAcceptRemote(prefixed, "a").Kind);
        Assert.Equal(AetherAdmitKind.Refused, host.TryAcceptRemote(raw, "peer-a").Kind);
        AssertUnchanged(store, host);
    }

    [Fact]
    public void A_contribution_for_another_writer_is_refused()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var own = Field("peer-a", "s1", "label", 10, "kept", 1);
        var foreign = Field("peer-b", "s1", "label", 11, "stolen", 1);
        var mixed = host.TryAcceptContribution([own, foreign], "peer-a");
        var relay = host.TryAcceptContribution([foreign], "peer-a");

        Assert.Equal(AetherAdmitKind.Refused, mixed.Kind);
        Assert.Equal(AetherAdmitKind.Refused, relay.Kind);
        Assert.Equal(0, mixed.RequestId);
        AssertUnchanged(store, host);

        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptContribution([own], "peer-a").Kind);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);
        Assert.Equal("kept", store.ResolveLww("entity-1", "label")?.Text);
    }

    [Fact]
    public void A_later_session_on_the_same_connection_is_admitted()
    {
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(Write("peer-a", "s1", 1, 10, "first"), "peer-a").Kind);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);

        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(Write("peer-a", "s2", 1, 11, "second"), "peer-a").Kind);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);

        Assert.Equal("second", store.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(2, store.ActorCursors.Count);
        Assert.Contains(store.ActorCursors, cursor => cursor.ActorId == AetherActorId.Format("peer-a", "s1") && cursor.Sequence == 1);
        Assert.Contains(store.ActorCursors, cursor => cursor.ActorId == AetherActorId.Format("peer-a", "s2") && cursor.Sequence == 1);
    }

    [Fact]
    public void Dropping_the_connection_keeps_loaded_state()
    {
        using var dir = new TempDir();
        var store = NewStore();
        var host = new AetherHost(store, 4096, 8);
        var archive = new AetherArchive(dir.Path);
        Assert.Equal(AetherAdmitKind.Queued, host.TryAcceptRemote(Write("peer-a", "s1", 1, 10, "kept"), "peer-a").Kind);
        Assert.Equal(AetherOutcomeKind.Applied, Assert.Single(host.Drain()).Kind);
        host.Save(archive);

        var loaded = archive.Load();
        Assert.Equal("kept", loaded.ResolveLww("entity-1", "label")?.Text);
        Assert.Equal(AetherActorId.Format("peer-a", "s1"), Assert.Single(loaded.ActorCursors).ActorId);
        Assert.Equal(1, loaded.RecordCount);
        Assert.Equal(store.Clock, loaded.Clock);
    }

    private static void AssertUnchanged(AetherReducer store, AetherHost host)
    {
        Assert.Equal(0, host.PendingCount);
        Assert.Equal(0, host.PendingBytes);
        Assert.Equal(0, store.Clock);
        Assert.Equal(0, store.SaveThrough);
        Assert.Equal(0, store.RecordCount);
        Assert.Empty(store.ActorCursors);
    }

    private static AetherOperation Write(string peerId, string sessionId, long sequence, long lamport, string text) =>
        new(
            peerId + "/" + sessionId + "/" + sequence,
            "entity-1",
            peerId,
            AetherActorId.Format(peerId, sessionId),
            sequence,
            lamport,
            new Dictionary<string, FieldValue> { ["label"] = FieldValue.Label(text) });

    private static StoredField Field(
        string peerId,
        string sessionId,
        string fieldId,
        long lamport,
        string text,
        long sequence) =>
        new(
            new RecordKey("entity-1", peerId, fieldId),
            FieldValue.Label(text),
            new FieldVersion(lamport, AetherActorId.Format(peerId, sessionId)),
            peerId,
            peerId + "/" + sessionId + "/" + sequence,
            sequence);

    private static AetherReducer NewStore()
    {
        var store = new AetherReducer();
        store.DefineField("label", "lww");
        return store;
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
