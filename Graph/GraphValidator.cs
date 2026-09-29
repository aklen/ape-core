namespace Ape.Core.Graph;

internal static class GraphValidator
{
    public static void Validate<TScratch>(GraphDefinition<TScratch> def)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in def.Hsm.States)
        {
            if (!states.Add(name))
            {
                throw new GraphCompileException(
                    "CG105",
                    $"HSM state '{name}' is declared more than once.");
            }
        }

        var stageIds = def.StageIndexByQualifiedId.Keys;

        foreach (var (state, targets) in def.Hsm.ActiveByState)
        {
            if (!states.Contains(state))
            {
                throw new GraphCompileException(
                    "CG103",
                    $"HSM activation set references undeclared state '{state}'.");
            }

            foreach (var target in targets)
                RequireTarget(target, stageIds, $"HSM state '{state}' activates unknown target '{target}'.");
        }

        foreach (var t in def.Hsm.Transitions)
        {
            if (t.FromState != null && !states.Contains(t.FromState))
            {
                throw new GraphCompileException(
                    "CG103",
                    $"HSM transition --{t.EventId}--> {t.ToState} references undeclared state '{t.FromState}'.");
            }

            if (!states.Contains(t.ToState))
            {
                throw new GraphCompileException(
                    "CG103",
                    $"HSM transition {FormatFrom(t)} --{t.EventId}--> '{t.ToState}' references an undeclared state.");
            }
        }

        var ports = new Dictionary<string, PortIR>(StringComparer.Ordinal);
        foreach (var port in def.Ports)
            ports[port.QualifiedId] = port;

        ValidateConnections(def, ports, stageIds);
        ValidateRaises(def, stageIds);
        ValidateWriters(def, stageIds);
    }

    private static void ValidateConnections<TScratch>(
        GraphDefinition<TScratch> def,
        Dictionary<string, PortIR> ports,
        IEnumerable<string> stageIds)
    {
        var enabledByState = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (state, targets) in def.Hsm.ActiveByState)
            enabledByState[state] = GraphActivation.EnabledStageIds(targets, stageIds);

        foreach (var conn in def.Connections)
        {
            if (!ports.TryGetValue(conn.From, out var producer))
            {
                throw new GraphCompileException(
                    "CG209",
                    $"Connection '{conn.From} -> {conn.To}' has unresolved producer '{conn.From}'.");
            }

            if (!ports.TryGetValue(conn.To, out var consumer))
            {
                throw new GraphCompileException(
                    "CG209",
                    $"Connection '{conn.From} -> {conn.To}' has unresolved consumer '{conn.To}'.");
            }

            if (producer.Direction != PortDirection.Out || consumer.Direction != PortDirection.In)
            {
                throw new GraphCompileException(
                    "CG213",
                    $"Connection '{conn.From} -> {conn.To}' must go Out → In.");
            }

            if (producer.ClrType != conn.ClrType || consumer.ClrType != conn.ClrType)
            {
                throw new GraphCompileException(
                    "CG201",
                    $"Connection '{conn.From} -> {conn.To}' has incompatible types " +
                    $"({TypeName(producer.ClrType)} / {TypeName(conn.ClrType)} / {TypeName(consumer.ClrType)}).");
            }

            if (!string.Equals(producer.ScratchSlot, consumer.ScratchSlot, StringComparison.Ordinal))
            {
                throw new GraphCompileException(
                    "CG201",
                    $"Connection '{conn.From} -> {conn.To}' binds scratch.{producer.ScratchSlot} " +
                    $"to scratch.{consumer.ScratchSlot}.");
            }

            foreach (var (state, enabled) in enabledByState)
            {
                var consumerOn = GraphActivation.PrefixActive(consumer.OwnerPrefix, enabled);
                var producerOn = GraphActivation.PrefixActive(producer.OwnerPrefix, enabled);
                if (consumerOn && !producerOn)
                {
                    throw new GraphCompileException(
                        "CG104",
                        $"{conn.To} consumes {conn.From}, " +
                        $"but '{producer.OwnerPrefix}' is disabled in HSM state \"{state}\".");
                }

                if (!consumerOn || !producerOn)
                    continue;

                if (string.IsNullOrEmpty(producer.OwnerPrefix))
                    continue;

                if (!TryTickOrder(
                        def,
                        producer.OwnerPrefix,
                        consumer.OwnerPrefix,
                        conn.From,
                        conn.To,
                        producer.ScratchSlot,
                        enabled,
                        state))
                    continue;
            }
        }
    }

    private static void ValidateRaises<TScratch>(GraphDefinition<TScratch> def, IEnumerable<string> stageIds)
    {
        foreach (var (source, eventId) in def.Raises)
        {
            var owner = source;
            var slash = source.LastIndexOf('/');
            if (slash >= 0)
                owner = source[..slash];

            if (!TargetResolves(owner, stageIds))
            {
                throw new GraphCompileException(
                    "CG209",
                    $"Raise '{source}' → {eventId} does not resolve to a stage.");
            }
        }
    }

    private static bool TryTickOrder<TScratch>(
        GraphDefinition<TScratch> def,
        string producerOwner,
        string consumerOwner,
        string fromPort,
        string toPort,
        string slot,
        HashSet<string> enabled,
        string state)
    {
        int producerLast;
        int consumerFirst;
        var haveOrder = TryLeafOrder(
            def, producerOwner, consumerOwner, slot, enabled, out producerLast, out consumerFirst);

        if (!haveOrder)
        {
            haveOrder =
                GraphActivation.TryIndexSpan(
                    producerOwner, enabled, def.StageIndexByQualifiedId, out _, out producerLast)
                && GraphActivation.TryIndexSpan(
                    consumerOwner, enabled, def.StageIndexByQualifiedId, out consumerFirst, out _);
        }

        if (!haveOrder)
            return false;

        if (producerLast >= consumerFirst)
        {
            throw new GraphCompileException(
                "CG208",
                $"{toPort} reads {fromPort} this tick, but '{consumerOwner}' " +
                $"(index {consumerFirst}) runs before '{producerOwner}' " +
                $"(index {producerLast}) in HSM state \"{state}\".");
        }

        return true;
    }

    private static bool TryLeafOrder<TScratch>(
        GraphDefinition<TScratch> def,
        string producerOwner,
        string consumerOwner,
        string slot,
        HashSet<string> enabled,
        out int producerLast,
        out int consumerFirst)
    {
        producerLast = int.MinValue;
        consumerFirst = int.MaxValue;
        var anyWriter = false;
        var anyReader = false;

        foreach (var access in def.Access)
        {
            if (!enabled.Contains(access.StageId))
                continue;

            if (InOwner(access.StageId, producerOwner) && access.Writes.Contains(slot, StringComparer.Ordinal))
            {
                if (!def.StageIndexByQualifiedId.TryGetValue(access.StageId, out var idx))
                    continue;
                anyWriter = true;
                if (idx > producerLast)
                    producerLast = idx;
            }

            if (InOwner(access.StageId, consumerOwner) && access.Reads.Contains(slot, StringComparer.Ordinal))
            {
                if (!def.StageIndexByQualifiedId.TryGetValue(access.StageId, out var idx))
                    continue;
                anyReader = true;
                if (idx < consumerFirst)
                    consumerFirst = idx;
            }
        }

        return anyWriter && anyReader;
    }

    private static void ValidateWriters<TScratch>(GraphDefinition<TScratch> def, IEnumerable<string> stageIds)
    {
        foreach (var (state, targets) in def.Hsm.ActiveByState)
        {
            var enabled = GraphActivation.EnabledStageIds(targets, stageIds);
            var writersBySlot = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var access in def.Access)
            {
                if (!enabled.Contains(access.StageId))
                    continue;

                foreach (var slot in access.Writes.Distinct(StringComparer.Ordinal))
                {
                    if (!writersBySlot.TryGetValue(slot, out var writers))
                    {
                        writers = new List<string>();
                        writersBySlot[slot] = writers;
                    }

                    writers.Add(access.StageId);
                }
            }

            foreach (var (slot, writers) in writersBySlot)
            {
                if (writers.Count <= 1)
                    continue;

                throw new GraphCompileException(
                    "CG207",
                    $"Multiple writers for scratch.{slot} in HSM state \"{state}\": " +
                    $"{string.Join(", ", writers)}. No deterministic arbitration rule is defined.");
            }
        }
    }

    private static bool InOwner(string stageId, string ownerPrefix)
    {
        if (string.IsNullOrEmpty(ownerPrefix))
            return true;
        return stageId == ownerPrefix || stageId.StartsWith(ownerPrefix + "/", StringComparison.Ordinal);
    }

    private static void RequireTarget(string target, IEnumerable<string> stageIds, string message)
    {
        if (TargetResolves(target, stageIds))
            return;

        throw new GraphCompileException("CG102", message);
    }

    private static bool TargetResolves(string target, IEnumerable<string> stageIds)
    {
        foreach (var id in stageIds)
        {
            if (string.Equals(id, target, StringComparison.Ordinal))
                return true;
            if (id.StartsWith(target + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string FormatFrom(HsmTransition t) => t.FromState ?? "*";

    private static string TypeName(Type type) => type.FullName ?? type.Name;
}
