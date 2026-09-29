namespace Ape.Core.Determinism;

/// <summary>
/// Deterministic ordering for DAG edges (plugin graph, pipeline stages). Tie-break: <see cref="IComparable{T}"/> on <typeparamref name="TNode"/> if implemented, else type name.
/// </summary>
public static class TopologicalSort
{
    /// <summary>
    /// Returns nodes in topological order. Throws <see cref="InvalidOperationException"/> if a cycle exists.
    /// </summary>
    /// <param name="nodes">All nodes.</param>
    /// <param name="dependencies">Direct dependencies: each node → nodes that must run before it.</param>
    public static IReadOnlyList<TNode> Sort<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, IEnumerable<TNode>> dependencies)
        where TNode : notnull
    {
        var list = nodes.Distinct().ToList();
        var depMap = list.ToDictionary(n => n, n => new HashSet<TNode>(dependencies(n)));

        var result = new List<TNode>();
        var ready = new SortedSet<TNode>(Comparer<TNode>.Create(CompareNodes));

        foreach (var n in list)
        {
            if (depMap[n].Count == 0)
                ready.Add(n);
        }

        while (ready.Count > 0)
        {
            var n = ready.Min!;
            ready.Remove(n);
            result.Add(n);

            foreach (var m in list)
            {
                if (depMap[m].Remove(n) && depMap[m].Count == 0)
                    ready.Add(m);
            }
        }

        if (result.Count != list.Count)
            throw new InvalidOperationException("TopologicalSort: cycle detected in dependency graph.");

        return result;

        static int CompareNodes(TNode a, TNode b)
        {
            if (ReferenceEquals(a, b))
                return 0;
            if (a is IComparable<TNode> cmp)
                return cmp.CompareTo(b);
            return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
        }
    }
}
