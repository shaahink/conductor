namespace Conductor.Models;

public sealed class StageConfig
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Expected session count from the plan doc; attempt budget = sessions * limits.stageSlackFactor.</summary>
    public int Sessions { get; set; } = 2;
    /// <summary>Optional stage-specific text appended to the session prompt.</summary>
    public string? Notes { get; set; }
    /// <summary>If true, the stage parks at <c>AwaitingOwner</c> even when green — the owner must
    /// approve before the orchestrator advances past it (B3.2).</summary>
    public bool OwnerGate { get; set; }
    /// <summary>Per-stage agent override (B7.1) — merged over the plan default. null = use plan default.
    /// The orchestrator resolves checkpoint ?? stage ?? plan default at session-start time.</summary>
    public AgentConfig? Agent { get; set; }
    /// <summary>Persona name to adopt for this stage (e.g. "deliver", "verify", "advise"). Resolved
    /// by <c>PersonaRegistry</c> into a system prompt. null = no persona (B7.2).</summary>
    public string? Persona { get; set; }
    /// <summary>Stage kind: "deliver" (default), "review" (self-review stage, B8.3).
    /// A review stage produces an advisory artifact, not mutations.</summary>
    public string Kind { get; set; } = "deliver";
    /// <summary>Stage IDs that must be completed (confirmed or skipped) before this stage becomes
    /// ready. Execution stays sequential; this only affects readiness ordering (B10.1).</summary>
    public List<string>? DependsOn { get; set; }
    /// <summary>Parent stage id for hierarchical display in tree + report (B10.2). null = root stage.
    /// Parent stages appear above their children in the plan tree with indentation.</summary>
    public string? ParentId { get; set; }
    /// <summary>Optional hook that runs before the stage's first session (B10.3). A non-zero exit
    /// blocks the stage and requests human attention.</summary>
    public HookConfig? PreHook { get; set; }
    /// <summary>Optional hook that runs after the stage is confirmed (B10.3). Best-effort: a non-zero
    /// exit is logged but never blocks completion.</summary>
    public HookConfig? PostHook { get; set; }
    /// <summary>Named workflow to use for this stage (e.g. "deliver-verify", "spike", "docs-only").
    /// Overrides the plan's DefaultWorkflow. Falls back to "deliver-verify" when both are unset (M3.1).</summary>
    public string? Workflow { get; set; }
    /// <summary>Per-stage overrides — drop QA, change model, skip gates/commit for one stage
    /// without altering the shared workflow definition (M3.2).</summary>
    public WorkflowOverrides? Overrides { get; set; }
    /// <summary>Declared file paths this stage touches (repo-relative). Used by the parallelism
    /// engine to detect collisions and serialize conflicting lanes (M3.3).</summary>
    public List<string>? PathClaims { get; set; }
    /// <summary>Per-stage QA dial (P2) — replaces the plan-wide <c>pipeline.qa</c> rule whole for
    /// this stage. null = inherit the plan dial (or classic behavior when neither is set).</summary>
    public QaRule? Qa { get; set; }
}
