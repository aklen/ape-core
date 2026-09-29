using System.Linq.Expressions;

namespace Ape.Core.Graph;

/// <summary>Compiler-visible scratch access for one leaf stage. Does not move data at runtime.</summary>
public sealed class StageContract<TScratch>
{
    private readonly List<string> _reads = new();
    private readonly List<string> _writes = new();

    public StageContract<TScratch> Reads<T>(Expression<Func<TScratch, T>> selector)
    {
        _reads.Add(ScratchSlot.Name(selector));
        return this;
    }

    public StageContract<TScratch> Writes<T>(Expression<Func<TScratch, T>> selector)
    {
        _writes.Add(ScratchSlot.Name(selector));
        return this;
    }

    internal IReadOnlyList<string> ReadSlots => _reads;

    internal IReadOnlyList<string> WriteSlots => _writes;
}
