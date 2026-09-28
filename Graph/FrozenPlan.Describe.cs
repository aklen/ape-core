using System.Text;

namespace Ape.Core.Graph;

public sealed partial class FrozenPlan<TScratch>
{
    /// <summary>Human-readable dump of execution order and per-state stage activation. Stable enough for tests and agents.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("FrozenPlan: ").AppendLine(Name);
        sb.AppendLine(new string('─', 40));
        sb.AppendLine();
        sb.Append("Stages: ").AppendLine(StageOrder.Count.ToString());
        sb.Append("Ports: ").AppendLine(_ports.Count.ToString());
        sb.Append("Connections: ").AppendLine(_connections.Count.ToString());
        sb.Append("Events: ").AppendLine(_raises.Count.ToString());
        sb.Append("HSM states: ").AppendLine(_hsm.States.Count.ToString());
        sb.AppendLine();
        sb.AppendLine("Execution order:");
        sb.AppendLine();
        for (var i = 0; i < StageOrder.Count; i++)
            sb.Append(i.ToString("00")).Append(' ').AppendLine(StageOrder[i]);

        if (_ports.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Ports:");
            sb.AppendLine();
            foreach (var port in _ports)
            {
                var dir = port.Direction == PortDirection.In ? "in " : "out";
                var freshness = port.Direction == PortDirection.Out || IsConnectedConsumer(port.QualifiedId)
                    ? "tick"
                    : "sampled";
                sb.Append("  ").Append(dir).Append(' ').Append(port.QualifiedId)
                    .Append(" : scratch.").Append(port.ScratchSlot)
                    .Append(" (").Append(port.ClrType.Name).Append(") [")
                    .Append(freshness).AppendLine("]");
            }
        }

        if (_connections.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Connections:");
            sb.AppendLine();
            foreach (var c in _connections)
                sb.Append("  ").AppendLine(c);
        }

        var declared = _access.Where(a => a.Reads.Count > 0 || a.Writes.Count > 0).ToArray();
        if (declared.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Scratch access:");
            sb.AppendLine();
            foreach (var a in declared)
            {
                sb.Append("  ").Append(a.StageId);
                if (a.Reads.Count > 0)
                    sb.Append("  R ").Append(string.Join(", ", a.Reads));
                if (a.Writes.Count > 0)
                    sb.Append("  W ").Append(string.Join(", ", a.Writes));
                sb.AppendLine();
            }
        }

        if (_raises.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Authoring events:");
            sb.AppendLine();
            foreach (var (source, eventId) in _raises)
                sb.Append("  ").Append(source).Append(" → ").AppendLine(eventId);
        }

        if (_hsm.States.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("State activation:");
            sb.AppendLine();
            AppendActivationMatrix(sb);
        }

        return sb.ToString().TrimEnd();
    }

    private bool IsConnectedConsumer(string portId)
    {
        foreach (var c in _connections)
        {
            var sep = c.IndexOf(" -> ", StringComparison.Ordinal);
            if (sep >= 0 && string.Equals(c[(sep + 4)..], portId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Graphviz DOT: execution chain plus HSM. Nested authoring is already flattened.</summary>
    public string ToDot()
    {
        var sb = new StringBuilder();
        sb.Append("digraph \"").Append(EscapeDot(Name)).AppendLine("\" {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box];");

        for (var i = 0; i < StageOrder.Count; i++)
        {
            var id = StageOrder[i];
            sb.Append("  s").Append(i).Append(" [label=\"").Append(EscapeDot(id)).AppendLine("\"];");
            if (i > 0)
                sb.Append("  s").Append(i - 1).Append(" -> s").Append(i).AppendLine(";");
        }

        if (_hsm.States.Count > 0)
        {
            sb.AppendLine("  subgraph cluster_hsm {");
            sb.AppendLine("    label=\"HSM\";");
            sb.AppendLine("    node [shape=ellipse];");
            foreach (var state in _hsm.States)
            {
                var shape = string.Equals(state, _hsm.InitialState, StringComparison.Ordinal)
                    ? "doublecircle"
                    : "ellipse";
                sb.Append("    h_").Append(DotId(state)).Append(" [label=\"").Append(EscapeDot(state))
                    .Append("\", shape=").Append(shape).AppendLine("];");
            }

            var anyEmitted = false;
            foreach (var t in _hsm.Transitions)
            {
                var from = t.FromState ?? "*";
                if (t.FromState == null && !anyEmitted)
                {
                    sb.AppendLine("    h_any [label=\"*\", shape=point];");
                    anyEmitted = true;
                }

                sb.Append("    h_").Append(DotId(from)).Append(" -> h_").Append(DotId(t.ToState))
                    .Append(" [label=\"").Append(EscapeDot(t.EventId)).AppendLine("\"];");
            }

            sb.AppendLine("  }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Explains one flattened stage: order index and which HSM states declare it active.</summary>
    public string ExplainStage(string qualifiedId)
    {
        if (!_stageIndex.TryGetValue(qualifiedId, out var index))
            throw new ArgumentException($"Unknown stage '{qualifiedId}'.", nameof(qualifiedId));

        var activeIn = new List<string>();
        foreach (var state in _hsm.States)
        {
            if (IsDeclaredActive(index, state))
                activeIn.Add(state);
        }

        var sb = new StringBuilder();
        sb.Append("Stage '").Append(qualifiedId).AppendLine("'");
        sb.Append("  index: ").AppendLine(index.ToString());
        sb.Append("  declared active in: ")
            .AppendLine(activeIn.Count == 0 ? "(none)" : string.Join(", ", activeIn));

        var related = _connections.Where(c =>
            c.StartsWith(qualifiedId, StringComparison.Ordinal) ||
            c.Contains(" -> " + qualifiedId, StringComparison.Ordinal) ||
            c.Contains('/' + qualifiedId, StringComparison.Ordinal)).ToArray();
        if (related.Length > 0)
        {
            sb.AppendLine("  connections:");
            foreach (var c in related)
                sb.Append("    ").AppendLine(c);
        }

        var raises = _raises.Where(r =>
            r.SourcePath.Equals(qualifiedId, StringComparison.Ordinal) ||
            r.SourcePath.StartsWith(qualifiedId + "/", StringComparison.Ordinal)).ToArray();
        if (raises.Length > 0)
        {
            sb.AppendLine("  events:");
            foreach (var (source, eventId) in raises)
                sb.Append("    ").Append(source).Append(" → ").AppendLine(eventId);
        }

        var access = _access.FirstOrDefault(a => a.StageId == qualifiedId);
        if (access != null && (access.Reads.Count > 0 || access.Writes.Count > 0))
        {
            if (access.Reads.Count > 0)
                sb.Append("  reads: ").AppendLine(string.Join(", ", access.Reads));
            if (access.Writes.Count > 0)
                sb.Append("  writes: ").AppendLine(string.Join(", ", access.Writes));
        }

        return sb.ToString().TrimEnd();
    }

    private void AppendActivationMatrix(StringBuilder sb)
    {
        var states = _hsm.States;
        var nameWidth = Math.Max(12, StageOrder.Count == 0 ? 0 : StageOrder.Max(s => s.Length));
        var colWidth = states.Count == 0 ? 8 : Math.Max(4, states.Max(s => s.Length) + 1);

        sb.Append(' ', nameWidth + 1);
        foreach (var state in states)
            sb.Append(state.PadRight(colWidth));
        sb.AppendLine();

        foreach (var (id, index) in StageOrder.Select((id, i) => (id, i)))
        {
            sb.Append(id.PadRight(nameWidth + 1));
            foreach (var state in states)
            {
                var mark = IsDeclaredActive(index, state) ? "●" : "";
                sb.Append(mark.PadRight(colWidth));
            }

            sb.AppendLine();
        }
    }

    private static string EscapeDot(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string DotId(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
