namespace Conductor.Models;

/// <summary>A single step in a workflow: what kind of session, with optional filters
/// and overrides.</summary>
public sealed class WorkflowStep
{
    public string Id { get; set; } = "";
    public SessionKind Kind { get; set; } = SessionKind.Deliver;
    public string? Model { get; set; }
    public string? RunIf { get; set; }
    public string? SkipIf { get; set; }
    public bool? Deliver { get; set; }
}
