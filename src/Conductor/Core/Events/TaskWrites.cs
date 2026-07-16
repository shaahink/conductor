namespace Conductor.Core.Events;

/// <summary>G2.1: the single source of task-write semantics. Both the MCP task tools
/// (<see cref="Conductor.Core.Integrations.McpTaskServer"/>) and the HTTP control plane
/// (<c>POST /tasks/update</c>, <c>POST /tasks/add</c>) build their <see cref="TaskStatusChanged"/>/
/// <see cref="TaskAdded"/> events here, so the two write ingresses can't drift. Pure — validates
/// against a folded <see cref="TaskGraph"/> and returns the event to emit; the caller owns the sink
/// (MCP journal vs run.db event log).</summary>
public static class TaskWrites
{
    public static readonly IReadOnlySet<string> ValidStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "todo", "in_progress", "done", "skipped" };

    /// <summary>Validate and build a status change. The returned event is emitted as-is; the fold
    /// (<see cref="TaskGraph.Fold"/>) still owns transition legality, so an illegal transition is a
    /// recorded no-op, exactly as the MCP path has always behaved.</summary>
    public static (TaskStatusChanged? Event, string? Error) BuildStatusChange(
        TaskGraph graph, string runId, string? taskId, string? status)
    {
        if (string.IsNullOrEmpty(taskId))
            return (null, "taskId is required");
        if (status is null || !ValidStatuses.Contains(status))
            return (null, $"invalid status: '{status}' (must be one of: {string.Join(", ", ValidStatuses)})");
        if (graph.Find(taskId) == null)
            return (null, $"task not found: {taskId}");

        return (new TaskStatusChanged { RunId = runId, TaskId = taskId, Status = status }, null);
    }

    /// <summary>P3: validate and build a detail edit (title, extra context, and/or declared paths —
    /// PF3). null = leave the field unchanged; an empty context clears it; an empty paths array
    /// clears the declared claims; a blank title is refused (a card must stay nameable). Path
    /// entries are trimmed and blanks dropped, so a replayed log never carries junk claims.
    /// At least one field must actually be given.</summary>
    public static (TaskDetailEdited? Event, string? Error) BuildDetailEdit(
        TaskGraph graph, string runId, string? taskId, string? title, string? context, string[]? paths = null)
    {
        if (string.IsNullOrEmpty(taskId))
            return (null, "taskId is required");
        if (graph.Find(taskId) == null)
            return (null, $"task not found: {taskId}");
        if (title is null && context is null && paths is null)
            return (null, "nothing to edit — give a title, a context, and/or paths");
        if (title is not null && string.IsNullOrWhiteSpace(title))
            return (null, "title cannot be blank");

        var cleanPaths = paths?.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        return (new TaskDetailEdited { RunId = runId, TaskId = taskId, Title = title?.Trim(), Context = context, Paths = cleanPaths }, null);
    }

    /// <summary>Validate and build a task-add: computes the next order within the checkpoint when the
    /// caller passes none, and generates a collision-free task id (<c>{cp}-a{order}</c>, suffixed on
    /// duplicates) — the exact algorithm the MCP handler used, now shared.</summary>
    public static (TaskAdded? Event, string? Error) BuildAdd(
        TaskGraph graph, string runId, string? checkpointId, string? title, int order, string source)
    {
        if (string.IsNullOrEmpty(checkpointId))
            return (null, "checkpointId is required");
        if (string.IsNullOrWhiteSpace(title))
            return (null, "title is required");

        var existing = graph.ForCheckpoint(checkpointId);
        var nextOrder = order > 0 ? order : (existing.Count > 0 ? existing.Max(t => t.Order) + 1 : 1);

        var taskId = $"{checkpointId}-a{nextOrder}";
        var attempt = 0;
        while (graph.Find(taskId) != null)
        {
            attempt++;
            taskId = $"{checkpointId}-a{nextOrder}.{attempt}";
        }

        return (new TaskAdded
        {
            RunId = runId,
            TaskId = taskId,
            CheckpointId = checkpointId,
            Title = title,
            Source = source,
            Order = nextOrder,
        }, null);
    }
}
