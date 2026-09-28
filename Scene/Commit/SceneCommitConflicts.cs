using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>Detects multiple participants writing the same scene property in one frame (stable order, still a design smell).</summary>
internal static class SceneCommitConflicts
{
    public static List<string> FindMultiWriterKeys(
        IReadOnlyList<(string ParticipantId, IReadOnlyList<ISceneCommitRequest> Ops)> batches)
    {
        var writers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (participantId, ops) in batches)
        {
            foreach (var op in ops)
            {
                if (!TryPropertyKey(op, out var key))
                    continue;
                if (!writers.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    writers[key] = set;
                }
                set.Add(participantId);
            }
        }

        var conflicts = new List<string>();
        foreach (var (key, set) in writers)
        {
            if (set.Count < 2)
                continue;
            var ids = string.Join(", ", set.OrderBy(x => x, StringComparer.Ordinal));
            conflicts.Add($"{key} written by [{ids}]");
        }

        conflicts.Sort(StringComparer.Ordinal);
        return conflicts;
    }

    private static bool TryPropertyKey(ISceneCommitRequest op, out string key)
    {
        switch (op)
        {
            case SetSceneNodePropertyCommitRequest p:
                key = $"node:{p.SceneKey}.{p.PropertyName}";
                return true;
            case SetSceneEntityPropertyCommitRequest e:
                key = $"entity:{e.SceneKey}.{e.PropertyName}";
                return true;
            case SetSceneNodePositionCommitRequest s:
                key = $"node:{s.NodeId}.Position";
                return true;
            default:
                key = "";
                return false;
        }
    }
}
