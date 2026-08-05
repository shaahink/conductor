namespace Conductor.Core;

/// <summary>
/// B9.2: decomposes a checkpoint into ordered advisory sub-tasks. Produced tasks are lightweight
/// break-points — the checkpoint table stays the verified contract (D-8, anti-pattern A16).
/// </summary>
public interface IPlanner
{
    IReadOnlyList<PlannedTask> Decompose(string checkpointId, string checkpointTitle, string stageNotes);
}

public sealed record PlannedTask(string Title, int Order);
