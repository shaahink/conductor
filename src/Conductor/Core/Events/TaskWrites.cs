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
    /// recorded no-op, exactly as the MCP path has always behaved. <paramref name="source"/> is the
    /// claim provenance (agent | human — who moved the card, W1.1).</summary>
    public static (TaskStatusChanged? Event, string? Error) BuildStatusChange(
        TaskGraph graph, string runId, string? taskId, string? status, string? source = null)
    {
        if (string.IsNullOrEmpty(taskId))
            return (null, "taskId is required");
        if (status is null || !ValidStatuses.Contains(status))
            return (null, $"invalid status: '{status}' (must be one of: {string.Join(", ", ValidStatuses)})");
        if (graph.Find(taskId) == null)
            return (null, $"task not found: {taskId}");

        return (new TaskStatusChanged { RunId = runId, TaskId = taskId, Status = status, Source = source }, null);
    }

    /// <summary>P3: validate and build a detail edit (title, extra context, and/or declared paths —
    /// PF3). null = leave the field unchanged; an empty context clears it; an empty paths array
    /// clears the declared claims; a blank title is refused (a card must stay nameable). Path
    /// entries are trimmed and blanks dropped, so a replayed log never carries junk claims.
    /// At least one field must actually be given.</summary>
    public static (TaskDetailEdited? Event, string? Error) BuildDetailEdit(
        TaskGraph graph, string runId, string? taskId, string? title, string? context, string[]? paths = null,
        string? qa = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (string.IsNullOrEmpty(taskId))
            return (null, "taskId is required");
        if (graph.Find(taskId) == null)
            return (null, $"task not found: {taskId}");
        if (title is null && context is null && paths is null && qa is null)
            return (null, "nothing to edit — give a title, a context, paths and/or qa");
        if (title is not null && string.IsNullOrWhiteSpace(title))
            return (null, "title cannot be blank");
        // W4.4: the item dial's vocabulary is deliberately small — a card says whether it wants
        // verification, not how to shape a stage.
        if (qa is not null && !WorkItemQa.IsValid(qa))
            return (null, $"invalid qa: '{qa}' (must be one of: {string.Join(", ", WorkItemQa.Valid)})");

        var cleanPaths = paths?.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        return (new TaskDetailEdited
        {
            RunId = runId, TaskId = taskId, Title = title?.Trim(), Context = context,
            Paths = cleanPaths, Qa = qa is null ? null : WorkItemQa.Normalize(qa),
        }, null);
    }

    /// <summary>Validate and build a task-add: computes the next order within the checkpoint when the
    /// caller passes none, and generates a collision-free task id (<c>{cp}-a{order}</c>, suffixed on
    /// duplicates) — the exact algorithm the MCP handler used, now shared.</summary>
    public static (TaskAdded? Event, string? Error) BuildAdd(
        TaskGraph graph, string runId, string? checkpointId, string? title, int order, string source,
        string? stageId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (string.IsNullOrWhiteSpace(title))
            return (null, "title is required");
        // W4.3: a rough card added at STAGE level is a checkpoint-kind item — the unit the engine
        // schedules — not a subtask needing a parent that does not exist yet. Without this, "we've
        // realised there's another requirement" had nowhere to land mid-run except an existing card.
        if (string.IsNullOrEmpty(checkpointId))
        {
            return string.IsNullOrWhiteSpace(stageId)
                ? (null, "checkpointId or stageId is required")
                : BuildStageLevelAdd(graph, runId, stageId, title, source);
        }

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
            // W1.1: cards added under a checkpoint are subtask-kind work items; the stage rides
            // along from the parent so views never have to re-derive it.
            Kind = WorkItemKinds.Subtask,
            StageId = graph.Find(checkpointId)?.StageId,
        }, null);
    }

    /// <summary>W4.3: the stage-level add. Ids follow the one convention the whole system reads —
    /// <c>{stage}.{n}</c> — taking the next free number after the stage's existing checkpoints, so
    /// the new card sorts and derives its stage exactly like a declared one.</summary>
    private static (TaskAdded? Event, string? Error) BuildStageLevelAdd(
        TaskGraph graph, string runId, string stageId, string title, string source)
    {
        stageId = stageId.Trim();
        if (stageId.Contains('.', StringComparison.Ordinal))
            return (null, $"stageId '{stageId}' looks like a checkpoint id — pass it as checkpointId");

        var siblings = graph.Checkpoints()
            .Where(c => string.Equals(c.StageId, stageId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var next = 1;
        foreach (var s in siblings)
        {
            var dot = s.TaskId.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && int.TryParse(s.TaskId[(dot + 1)..], out var n) && n >= next) next = n + 1;
        }

        var id = $"{stageId}.{next}";
        while (graph.Find(id) != null) id = $"{stageId}.{++next}";

        return (new TaskAdded
        {
            RunId = runId,
            TaskId = id,
            CheckpointId = id,   // a checkpoint is its own parent, as every seeded one is
            Title = title.Trim(),
            Source = source,
            Order = siblings.Count + 1,
            Kind = WorkItemKinds.Checkpoint,
            StageId = stageId,
        }, null);
    }
}
