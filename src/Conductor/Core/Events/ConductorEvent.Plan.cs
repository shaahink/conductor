namespace Conductor.Core.Events;

/// <summary>G3.2: the live plan was swapped at a session boundary (Face edit / import / CLI
/// `plan reload` while the run was up) — the next session runs against the new plan.</summary>
public sealed record PlanReloaded : ConductorEvent
{
    public required int PlanVersion { get; init; }
    public int Stages { get; init; }
    public int Gates { get; init; }
}
