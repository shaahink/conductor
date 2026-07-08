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
}
