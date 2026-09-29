namespace Ape.Core.Determinism;

/// <summary>
/// Stable merge of already-sampled per-source inputs into one list.
/// Independent sensors are usually kept as separate lists on <see cref="SampleFrame"/>; use this only when a single total order is required.
/// </summary>
public static class SampleFrameMerge
{
    /// <summary>
    /// Sort: observation time, optional source priority (lower wins), SourceId ordinal, SourceSequence.
    /// Missing observation time sorts last. Missing sequence sorts last within the same source.
    /// </summary>
    public static IReadOnlyList<SampleFrameInput> Merge(
        IEnumerable<SampleFrameInput> inputs,
        IReadOnlyDictionary<string, int>? sourcePriority = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var list = inputs.ToList();
        list.Sort((a, b) => Compare(a, b, sourcePriority));
        return list;
    }

    private static int Compare(
        SampleFrameInput a,
        SampleFrameInput b,
        IReadOnlyDictionary<string, int>? sourcePriority)
    {
        var ta = a.ObservationTime ?? DateTime.MaxValue;
        var tb = b.ObservationTime ?? DateTime.MaxValue;
        var byTime = ta.CompareTo(tb);
        if (byTime != 0)
            return byTime;

        var pa = Priority(a.SourceId, sourcePriority);
        var pb = Priority(b.SourceId, sourcePriority);
        var byPri = pa.CompareTo(pb);
        if (byPri != 0)
            return byPri;

        var byId = string.CompareOrdinal(a.SourceId, b.SourceId);
        if (byId != 0)
            return byId;

        var sa = a.SourceSequence ?? long.MaxValue;
        var sb = b.SourceSequence ?? long.MaxValue;
        return sa.CompareTo(sb);
    }

    private static int Priority(string sourceId, IReadOnlyDictionary<string, int>? sourcePriority)
    {
        if (sourcePriority != null && sourcePriority.TryGetValue(sourceId, out var p))
            return p;
        return int.MaxValue;
    }
}
