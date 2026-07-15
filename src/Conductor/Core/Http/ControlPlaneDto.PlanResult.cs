namespace Conductor.Core.Http;

/// <summary>One field's before/after in a plan diff (field name, old value, new value).</summary>
public sealed record PlanFieldChangeDto(string Field, string? Old, string? New);

/// <summary>Result of a plan edit (<c>POST /plan/edit</c>): whether it saved, why not, and the new
/// plan version so the TUI can confirm the write landed.</summary>
public sealed record PlanMutationResultDto(bool Ok, string? Error, int PlanVersion);

/// <summary>Result of a plan import (<c>POST /plan/import</c>): the diff (always, for preview) plus, when
/// applied, the new plan version. <c>Ok=false</c> with a reason when the source wasn't a structured plan.</summary>
public sealed record PlanImportResultDto(bool Ok, string? Error, PlanDiffDto Diff, bool Applied, int PlanVersion);
