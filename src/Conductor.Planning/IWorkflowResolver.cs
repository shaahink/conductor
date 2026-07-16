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

    /// <summary>P4: the complete "what comes after this session" decision. Walks the workflow from
    /// the recorded index, consuming verification steps as skipped-as-passed when
    /// <paramref name="skipVerification"/> is set (each such hop re-evaluates with
    /// verifier.passed = true — the collapse the engine used to do by recursion), and records the
    /// final index. The engine only effects the answer: logs the hops, confirms checkpoints for the
    /// skips, and populates the pending context for the resolved kind.</summary>
    WorkflowAdvance Advance(WorkflowDefinition workflow, Dictionary<string, int> stepIndices,
        string stageId, WorkflowRuntimeVars vars, bool skipVerification);

    /// <summary>P4: the session-START kind decision. A recorded index IS this session's step — the
    /// previous advance resolved and recorded it — so it is consumed WITHOUT advancing; only a
    /// stage's very first resolution advances (from -1, with blank facts). A verification step
    /// downgrades to Deliver when <paramref name="skipVerification"/> is set; an exhausted or empty
    /// workflow defaults to Deliver.</summary>
    SessionKind ResolveStartKind(WorkflowDefinition workflow, Dictionary<string, int> stepIndices,
        string stageId, bool skipVerification);
}
