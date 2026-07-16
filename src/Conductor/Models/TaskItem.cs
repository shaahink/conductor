namespace Conductor.Models;

/// <summary>
/// A single sub-task beneath a checkpoint (B9.1). Tasks are advisory break-points — the checkpoint
/// table stays the verified contract (D-8). Anti-pattern A16: keep lightweight; over-planning is banned.
/// </summary>
public sealed class TaskItem
{
    public string TaskId { get; set; } = "";
    public string CheckpointId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "todo";
    public string Source { get; set; } = "";
    public int Order { get; set; }

    /// <summary>P3: owner-provided extra context for this task — structured task data that becomes
    /// the editable "extra context" block of the task's prompt composition. Empty = none.</summary>
    public string Context { get; set; } = "";

    /// <summary>PF3: repo-relative paths this card is DECLARED to touch — the real task data behind
    /// <c>ReadyItem.PathClaims</c>, so a multi-item session refuses to co-claim checkpoints whose
    /// open cards declare overlapping paths. Empty = no declared claims (the common case).</summary>
    public List<string> Paths { get; set; } = new();
}
