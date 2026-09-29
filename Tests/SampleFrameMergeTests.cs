using Ape.Core.Determinism;

namespace Ape.Core.Tests;

public class SampleFrameMergeTests
{
    [Fact]
    public void Merge_orders_by_observation_time_then_priority_then_source_id_then_sequence()
    {
        var a2 = new SampleFrameInput("A", 2, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc), "x");
        var b1 = new SampleFrameInput("B", 1, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc), "x");
        var a1 = new SampleFrameInput("A", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "x");
        var merged = SampleFrameMerge.Merge(
            [b1, a2, a1],
            sourcePriority: new Dictionary<string, int> { ["B"] = 0, ["A"] = 1 });

        Assert.Equal(new[] { "A:1", "B:1", "A:2" }, merged.Select(i => $"{i.SourceId}:{i.SourceSequence}").ToArray());
    }
}
