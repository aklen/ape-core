namespace Ape.Core.Graph;

/// <summary>Per-plugin mutable runtime: HSM state, event queue, enabled-stage mask. Not part of the frozen plan.</summary>
public sealed class PlanRuntime
{
    private ulong _enabledMask;

    public string CurrentState { get; set; } = "";

    public Queue<string> Events { get; } = new();

    internal ulong EnabledMask => _enabledMask;

    internal void SetMask(ulong mask) => _enabledMask = mask;

    public bool IsStageEnabled(int stageIndex) =>
        stageIndex >= 0 && stageIndex < 64 && ((_enabledMask >> stageIndex) & 1) != 0;

    public void EnqueueEvent(string eventId) => Events.Enqueue(eventId);
}
