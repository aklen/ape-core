namespace Ape.Core.Graph;

/// <summary>Fluent HSM definition for <see cref="ComponentGraph{TScratch}.Hsm"/>.</summary>
public sealed class HsmBuilder
{
    private string? _initial;
    private readonly List<string> _states = new();
    private readonly Dictionary<string, string[]> _activeByState = new(StringComparer.Ordinal);
    private readonly List<HsmTransition> _transitions = new();

    /// <summary>
    /// Declares a state and its compiled activation set. Prefixes address nested authoring
    /// (<c>"decode"</c> → <c>decode/*</c>). Empty <paramref name="active"/> means no stages run.
    /// </summary>
    public HsmBuilder State(string name, params string[] active)
    {
        _states.Add(name);
        _activeByState[name] = active;
        _initial ??= name;
        return this;
    }

    public HsmBuilder On(string fromState, string eventId, string toState)
    {
        _transitions.Add(new HsmTransition
        {
            FromState = fromState,
            EventId = eventId,
            ToState = toState,
        });
        return this;
    }

    public HsmBuilder OnAny(string eventId, string toState)
    {
        _transitions.Add(new HsmTransition
        {
            FromState = null,
            EventId = eventId,
            ToState = toState,
        });
        return this;
    }

    internal HsmDefinition Build() => new()
    {
        InitialState = _initial ?? "Idle",
        States = _states.Count > 0 ? _states.ToArray() : ["Idle"],
        ActiveByState = _activeByState.Count > 0
            ? _activeByState
            : new Dictionary<string, string[]>(StringComparer.Ordinal) { ["Idle"] = [] },
        Transitions = _transitions,
    };
}
