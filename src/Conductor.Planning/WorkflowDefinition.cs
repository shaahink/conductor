namespace Conductor.Planning;

/// <summary>A named, declarative workflow: an ordered list of session steps with conditionals.
/// Replaces the hardcoded Deliver→Verify→Fix cycle with data-driven orchestration (M3.1).</summary>
public sealed class WorkflowDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<WorkflowStep> Steps { get; set; } = [];
    public bool Repeat { get; set; } = true;
}
