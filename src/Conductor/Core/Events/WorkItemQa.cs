namespace Conductor.Core.Events;

/// <summary>
/// W4.4: the per-item QA dial's vocabulary (criterion 5 — "sometimes QA a specific task; sometimes
/// just deliver tasks one-by-one with no verify step").
///
/// Deliberately smaller than the plan/stage dial's modes: an item says only whether it wants
/// verification, not how a whole stage should be shaped. <c>inherit</c> is the absence of an
/// override and stores as empty, so a card carries an override only when someone set one.
/// </summary>
public static class WorkItemQa
{
    public const string Inherit = "inherit";
    public const string Verify = "verify";
    public const string Off = "off";

    public static readonly IReadOnlySet<string> Valid =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Inherit, Verify, Off };

    public static bool IsValid(string? value) => value != null && Valid.Contains(value.Trim());

    public static bool IsInherit(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), Inherit, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored form: "" for inherit, else the lowercase mode.</summary>
    public static string Normalize(string? value) =>
        IsInherit(value) ? "" : value!.Trim().ToLowerInvariant();
}
