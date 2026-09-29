namespace Ape.Core.Graph;

internal static class GraphActivation
{
    public static HashSet<string> EnabledStageIds(
        IReadOnlyList<string> activeTargets,
        IEnumerable<string> stageIds)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in activeTargets)
        {
            foreach (var id in stageIds)
            {
                if (id == target || id.StartsWith(target + "/", StringComparison.Ordinal))
                    enabled.Add(id);
            }
        }

        return enabled;
    }

    public static bool PrefixActive(string ownerPrefix, HashSet<string> enabledStages)
    {
        if (string.IsNullOrEmpty(ownerPrefix))
            return true;

        foreach (var id in enabledStages)
        {
            if (id == ownerPrefix || id.StartsWith(ownerPrefix + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool TryIndexSpan(
        string ownerPrefix,
        HashSet<string> enabledStages,
        IReadOnlyDictionary<string, int> stageIndex,
        out int min,
        out int max)
    {
        min = int.MaxValue;
        max = int.MinValue;
        var any = false;

        foreach (var id in enabledStages)
        {
            if (id != ownerPrefix && !id.StartsWith(ownerPrefix + "/", StringComparison.Ordinal))
                continue;
            if (!stageIndex.TryGetValue(id, out var idx))
                continue;

            any = true;
            if (idx < min)
                min = idx;
            if (idx > max)
                max = idx;
        }

        return any;
    }
}
