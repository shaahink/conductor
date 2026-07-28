using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>Engine-side convenience over the pure <see cref="IWorkflowResolver"/> seam (P0): the
/// library's Resolve is agnostic (names + custom definitions in), so the engine keeps its ergonomic
/// Resolve(plan, stage) shape here — a thin adapter, no logic. Since P2 the QA dial is consulted
/// first: a set dial projects onto the workflow it names, superseding the stage/plan workflow —
/// there is deliberately no dial-blind overload, so every resolution honors the dial.</summary>
public static class WorkflowResolverExtensions
{
    public static WorkflowDefinition Resolve(this IWorkflowResolver resolver, PlanConfig plan, StageConfig stage, IQaPolicy qa,
        string? itemQa = null)
        => resolver.Resolve(qa.Project(plan, stage, itemQa).WorkflowName ?? stage.Workflow, plan.DefaultWorkflow, plan.Workflows);
}
