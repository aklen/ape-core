namespace Ape.Core.Graph;

internal sealed class GraphDefinition<TScratch>
{
    public required string Name { get; init; }

    public required IReadOnlyList<IStage<TScratch>> Stages { get; init; }

    public required IReadOnlyDictionary<string, int> StageIndexByQualifiedId { get; init; }

    public required IReadOnlyList<PortIR> Ports { get; init; }

    public required IReadOnlyList<ConnectionIR> Connections { get; init; }

    public required IReadOnlyList<StageAccessIR> Access { get; init; }

    public required IReadOnlyList<(string SourcePath, string EventId)> Raises { get; init; }

    public required HsmDefinition Hsm { get; init; }
}

internal enum PortDirection
{
    In,
    Out,
}

/// <summary>
/// Temporal contract for a scratch value. Not a runtime flag: the compiler uses it to decide
/// who must produce the value and when.
/// <see cref="Sampled"/> — SAMPLE_t wrote it; no graph ordering.
/// <see cref="CurrentTick"/> — a graph writer this frame, before the reader.
/// <see cref="Latched"/> — a prior tick's write; needs an initialization story before it can be proven.
/// </summary>
internal enum PortFreshness
{
    Sampled,
    CurrentTick,
    Latched,
}

internal sealed class PortIR
{
    public required string QualifiedId { get; init; }

    public required string OwnerPrefix { get; init; }

    public required PortDirection Direction { get; init; }

    public required PortFreshness Freshness { get; init; }

    public required Type ClrType { get; init; }

    public required string ScratchSlot { get; init; }
}

internal sealed class ConnectionIR
{
    public required string From { get; init; }

    public required string To { get; init; }

    public required Type ClrType { get; init; }
}

internal sealed class StageAccessIR
{
    public required string StageId { get; init; }

    public required IReadOnlyList<string> Reads { get; init; }

    public required IReadOnlyList<string> Writes { get; init; }
}

internal sealed class HsmDefinition
{
    public required string InitialState { get; init; }

    public IReadOnlyList<string> States { get; init; } = [];

    public IReadOnlyDictionary<string, string[]> ActiveByState { get; init; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public required IReadOnlyList<HsmTransition> Transitions { get; init; }
}

internal sealed class HsmTransition
{
    public string? FromState { get; init; }

    public required string EventId { get; init; }

    public required string ToState { get; init; }
}
