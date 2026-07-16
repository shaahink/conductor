using Conductor.Planning;

namespace PlanLint;

/// <summary>The slice of a plan file the planning library can decide on — parsed with the library's
/// own rule types (PipelineRules, QaRule, WorkflowOverrides, WorkflowDefinition), which is the
/// point: the schema travels with the library, not the engine.</summary>
internal sealed class PlanLite
{
    public string? Name { get; set; }
    public string? DefaultWorkflow { get; set; }
    public PipelineRules? Pipeline { get; set; }
    public Dictionary<string, WorkflowDefinition>? Workflows { get; set; }
    public List<StageLite> Stages { get; set; } = [];
}

internal sealed class StageLite
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Workflow { get; set; }
    public QaRule? Qa { get; set; }
    public WorkflowOverrides? Overrides { get; set; }
}
