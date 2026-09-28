using System.Security.Cryptography;
using Ape.Core.Replication;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Stable digest of the authoritative Scene's replicated payloads and lookup identity.
/// It is not a hash of process-local references or replication transport state.
/// Compare only with the same module/MessagePack schema versions.
/// </summary>
public static class SceneStateDigest
{
    private sealed record Entry(
        string Kind, string TypeName, string TypeId, string Id, string Path, byte[] Payload);

    public static string Capture(ISceneRead scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var entries = new List<Entry>();
        foreach (var node in scene.GetAllNodes())
            entries.Add(Describe("node", node, ""));
        foreach (var entity in scene.GetAllEntities())
            entries.Add(Describe("entity", entity, entity.TypeId));

        var ordered = entries.OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.TypeName, StringComparer.Ordinal)
            .ThenBy(e => e.TypeId, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Path, StringComparer.Ordinal);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var entry in ordered)
            {
                writer.Write(entry.Kind);
                writer.Write(entry.TypeName);
                writer.Write(entry.TypeId);
                writer.Write(entry.Id);
                writer.Write(entry.Path);
                writer.Write(entry.Payload.Length);
                writer.Write(entry.Payload);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static Entry Describe(string kind, IReplica replica, string typeId)
    {
        lock (replica.SyncRoot)
        {
            return new Entry(kind, replica.GetType().FullName ?? replica.GetType().Name,
                typeId, replica.Id, replica.UniquePath, replica.Serialize());
        }
    }
}
