namespace Conductor.Core.Http;

/// <summary>Plan-level limits surfaced to the TUI editor (read-only today; a future edit target).</summary>
public sealed record PlanLimitsDto(
    int StallMinutes, int SessionTimeoutMinutes, decimal? MaxRunCostUsd, long? MaxRunTokens, int VerifierThreshold);

/// <summary>One field edit the TUI applies to the plan: retarget an existing stage/gate/plan field.
/// <c>Target</c> ∈ stage|gate|plan; <c>Id</c> is the stage id / gate name (empty for plan-level);
/// <c>Field</c> is the property (title, model, workflow, kind, sessions, notes, persona, command, tier,
/// timeout, gatePolicy, defaultWorkflow); <c>Value</c> is the new value (null clears optional fields).</summary>
public sealed record PlanEditDto(string Target, string Id, string Field, string? Value);

/// <summary>A batch of edits applied atomically: all validate against the plan or none are saved (M6.3).</summary>
public sealed record PlanEditRequestDto(IReadOnlyList<PlanEditDto> Edits);
