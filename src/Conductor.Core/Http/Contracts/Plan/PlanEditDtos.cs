namespace Conductor.Core.Http;

/// <summary>Plan-level limits surfaced to the TUI editor — editable since G3.3 via the
/// <c>limits</c> target on <c>POST /plan/edit</c>, live-applied at the next session boundary.</summary>
public sealed record PlanLimitsDto(
    int StallMinutes, int SessionTimeoutMinutes, decimal? MaxRunCostUsd, long? MaxRunTokens, int VerifierThreshold,
    int? MaxSessions = null,
    // P5: session-token rollover — null = OFF (the default, honestly labeled in the Face).
    long? MaxSessionTokens = null, double? SoftBreakRatio = null);

/// <summary>One edit the TUI applies to the plan. <c>Op</c> ∈ set|add|delete (absent/null ⇒ "set" for
/// back-compat). <c>Target</c> ∈ stage|gate|plan|limits|qa|telegram; <c>Id</c> is the stage id / gate name (empty
/// for plan-level). For a <b>set</b>, <c>Field</c> is the property (title, model, workflow, kind, sessions,
/// notes, persona, command, tier, timeout, gatePolicy, defaultWorkflow) and <c>Value</c> the new value
/// (null clears optional fields). For an <b>add</b> (stage|gate only), <c>Id</c> is the new id/name and
/// <c>Value</c> the initial title (stage) / command (gate); other fields take schema defaults, editable
/// after. For a <b>delete</b> (stage|gate only), only <c>Id</c> is used. add/delete still round-trip the
/// same atomic validate-then-save gate, so an edit that would break the plan (e.g. deleting a
/// depended-on stage) is rejected whole.</summary>
public sealed record PlanEditDto(string Target, string Id, string Field, string? Value, string? Op = null);

/// <summary>A batch of edits applied atomically: all validate against the plan or none are saved (M6.3).</summary>
public sealed record PlanEditRequestDto(IReadOnlyList<PlanEditDto> Edits);
