using Ape.Core.Determinism;

namespace Ape.Core.Tests;

public class IngressBufferTests
{
    [Fact]
    public void DrainOrdered_sorts_by_sequence_not_enqueue_order()
    {
        var buffer = new IngressBuffer<int>(IngressOrdering.SourceSequence);
        buffer.Enqueue(10, explicitSequence: 3, sourceEpoch: "s");
        buffer.Enqueue(20, explicitSequence: 1, sourceEpoch: "s");
        buffer.Enqueue(30, explicitSequence: 2, sourceEpoch: "s");

        var drained = buffer.DrainOrdered();
        Assert.Equal(new[] { 20, 30, 10 }, drained.Select(e => e.Payload).ToArray());
        Assert.Equal(new[] { 1L, 2L, 3L }, drained.Select(e => e.Sequence).ToArray());
    }

    [Fact]
    public void DrainOrdered_empty_returns_empty_and_second_drain_stays_empty()
    {
        var buffer = new IngressBuffer<string>();
        Assert.Empty(buffer.DrainOrdered());
        buffer.Enqueue("x");
        Assert.Single(buffer.DrainOrdered());
        Assert.Empty(buffer.DrainOrdered());
    }

    [Fact]
    public void CaptureOrder_auto_sequence_follows_enqueue_order()
    {
        var buffer = new IngressBuffer<char>(IngressOrdering.CaptureOrder);
        buffer.Enqueue('a');
        buffer.Enqueue('b');
        buffer.Enqueue('c');

        var drained = buffer.DrainOrdered();
        Assert.Equal(new[] { 'a', 'b', 'c' }, drained.Select(e => e.Payload).ToArray());
        Assert.Equal(new[] { 1L, 2L, 3L }, drained.Select(e => e.Sequence).ToArray());
    }
}
