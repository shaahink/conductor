namespace Conductor.Core.Http;

/// <summary>One field's before/after in a plan diff (field name, old value, new value).</summary>
public sealed record PlanFieldChangeDto(string Field, string? Old, string? New);

/// <summary>Result of a plan edit (<c>POST /plan/edit</c>): whether it saved, why not, and the new
/// plan version so the TUI can confirm the write landed.</summary>
public sealed record PlanMutationResultDto(bool Ok, string? Error, int PlanVersion);

/// <summary>Result of a plan import (<c>POST /plan/import</c>): the diff (always, for preview) plus, when
/// applied, the new plan version. <c>Interpreter</c> says what turned the source into a plan —
/// <c>"structured"</c> for the deterministic parse, or the advisor model that read the prose (G1.1).</summary>
public sealed record PlanImportResultDto(bool Ok, string? Error, PlanDiffDto Diff, bool Applied, int PlanVersion, string? Interpreter = null);
