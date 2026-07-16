using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>Engine-side convenience over the pure <see cref="IWorkflowResolver"/> seam (P0): the
/// library's Resolve is agnostic (names + custom definitions in), so the engine keeps its ergonomic
/// Resolve(plan, stage) shape here — a thin adapter, no logic.</summary>
public static class WorkflowResolverExtensions
{
    public static WorkflowDefinition Resolve(this IWorkflowResolver resolver, PlanConfig plan, StageConfig stage)
        => resolver.Resolve(stage.Workflow, plan.DefaultWorkflow, plan.Workflows);
}
