namespace Conductor.Planning;

/// <summary>A ready-to-work item (a not-done checkpoint or task card) as the assignment policy sees
/// it — a POCO of facts, no engine types (P1).</summary>
public sealed class ReadyItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Repo-relative paths this item is declared to touch. null/empty = no declared claims
    /// (the common case today); conflicts are only ever detected between DECLARED claims.</summary>
    public IReadOnlyList<string>? PathClaims { get; set; }
}
