namespace Conductor.Models;

/// <summary>Per-stage workflow override — drop QA, change model, or skip commit (M3.2).</summary>
public sealed class WorkflowOverrides
{
    public string? Model { get; set; }
    public bool? SkipVerification { get; set; }
    public bool? SkipGates { get; set; }
    public bool? SkipCommit { get; set; }
}
