namespace Conductor.Planning;

/// <summary>The planning seam (P0): the engine asks these questions and executes the answers; the
/// library owns the decisions. Implementations must stay pure — a deterministic function of the
/// arguments, no IO, no clocks, no engine types. <see cref="WorkflowEngine"/> is the default
/// implementation, wired via DI (the same pattern as the engine's IPlanner).</summary>
public interface IWorkflowResolver
{
    /// <summary>Resolve the effective workflow by name: the stage-level name wins, then the plan
    /// default, then the built-in "deliver-verify". <paramref name="customWorkflows"/> are the
    /// plan-author-defined definitions consulted when the name is not a built-in.</summary>
    WorkflowDefinition Resolve(string? stageWorkflow, string? defaultWorkflow,
        IReadOnlyDictionary<string, WorkflowDefinition>? customWorkflows);

    /// <summary>The next step to execute given the current step index and the previous session's
    /// runtime facts. Null when the workflow is exhausted (stage complete).</summary>
    WorkflowStep? GetNextStep(WorkflowDefinition workflow, int currentStepIndex, WorkflowRuntimeVars vars);

    /// <summary>Resolve the next step AND durably record its index in <paramref name="stepIndices"/>
    /// in one call — the single source of truth both resolution call sites share (two independent
    /// read-resolve-write cycles drifted out of sync once; see the implementation's remarks).</summary>
    WorkflowStep? ResolveAndRecordStep(WorkflowDefinition workflow, Dictionary<string, int> stepIndices,
        string stageId, WorkflowRuntimeVars vars);

    /// <summary>The first step that passes its conditionals.</summary>
    WorkflowStep? GetInitialStep(WorkflowDefinition workflow, WorkflowRuntimeVars vars);

    /// <summary>Evaluate a RunIf / SkipIf expression ("!verifier.passed", "verifier.score >= 80", …)
    /// against the runtime facts.</summary>
    bool EvaluateCondition(string expr, WorkflowRuntimeVars vars);
}
