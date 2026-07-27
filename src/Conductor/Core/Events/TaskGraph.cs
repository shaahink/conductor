using Conductor.Models;

namespace Conductor.Core.Events;

/// <summary>
/// B9.1 (unified in W1.1): folds the task-event family into the live work graph — checkpoints and
/// sub-tasks in one projection. Pure projection — replay a log segment from any point reproduces
/// the same state (the W1.1 truth gate). Allowed status transitions: todo → in_progress → done
/// (also todo → skipped), the G2 reopen moves back out of done/skipped, blocked in/out of the open
/// states (tracker BLOCKED rows, W1.1), and same → same as a metadata refresh so a repeated
/// done-claim can update its commit/evidence. <see cref="CheckpointConfirmed"/> folds here too:
/// claims flip status, the engine's confirmation sets <see cref="TaskItem.Confirmed"/>.
/// </summary>
public sealed class TaskGraph
{
    private readonly List<TaskItem> _tasks = new();
    private readonly Dictionary<string, TaskItem> _byId = new(StringComparer.Ordinal);

    public IReadOnlyList<TaskItem> Tasks => _tasks;
    public int Count => _tasks.Count;
    public long LastEventSeq { get; private set; }

    /// <summary>Fold a batch of events, respecting the immutable log order.</summary>
    public void Fold(IEnumerable<ConductorEvent> events)
    {
        foreach (var evt in events)
        {
            LastEventSeq = Math.Max(LastEventSeq, evt.Seq);
            switch (evt)
            {
                case TaskAdded ta:
                    if (_byId.ContainsKey(ta.TaskId))
                        break; // duplicate — skip (first write wins, event-sourced)
                    // Pre-W1 events carry no kind/stage: seeds always wrote checkpoint cards with
                    // TaskId == CheckpointId, so that equality is the legacy discriminator, and the
                    // split-on-first-dot default (TrackerParser's Loom convention) recovers the stage.
                    var kind = ta.Kind ?? (ta.TaskId.Equals(ta.CheckpointId, StringComparison.Ordinal)
                        ? WorkItemKinds.Checkpoint : WorkItemKinds.Subtask);
                    var item = new TaskItem
                    {
                        TaskId = ta.TaskId,
                        CheckpointId = ta.CheckpointId,
                        Title = ta.Title,
                        Status = "todo",
                        Source = ta.Source,
                        Order = ta.Order,
                        Kind = kind,
                        StageId = ta.StageId ?? ta.CheckpointId.Split('.')[0],
                    };
                    _tasks.Add(item);
                    _byId[ta.TaskId] = item;
                    break;

                case TaskStatusChanged sc:
                    if (_byId.TryGetValue(sc.TaskId, out var existing))
                    {
                        if (IsValidTransition(existing.Status, sc.Status))
                        {
                            existing.Status = sc.Status;
                            // A done-claim carries its attribution; keep the last non-null values so
                            // a replay reproduces the checkpoint columns byte-for-byte (W1.1).
                            if (sc.Commit is { Length: > 0 }) existing.Commit = sc.Commit;
                            if (sc.Evidence is { Length: > 0 }) existing.Evidence = sc.Evidence;
                        }
                    }
                    break;

                case TaskDetailEdited de:
                    if (_byId.TryGetValue(de.TaskId, out var edited))
                    {
                        // null = unchanged; a blank title is refused at write time (TaskWrites), so a
                        // replayed log can never blank a card. Context empty = cleared, by design;
                        // same for the declared paths (PF3) — an empty array clears the claims.
                        if (!string.IsNullOrWhiteSpace(de.Title)) edited.Title = de.Title;
                        if (de.Context != null) edited.Context = de.Context;
                        if (de.Paths != null) edited.Paths = [.. de.Paths];
                    }
                    break;

                case CheckpointConfirmed cc:
                    // W1.1: confirmation is the engine's verdict, not a claim — set-only, never
                    // cleared by replay (matches the M4.1 confirmed column it replaces).
                    if (_byId.TryGetValue(cc.CheckpointId, out var confirmed))
                        confirmed.Confirmed = true;
                    break;
            }
        }
    }

    public TaskItem? Find(string taskId) =>
        _byId.TryGetValue(taskId, out var t) ? t : null;

    /// <summary>W1.1: the checkpoint-kind items — what the dropped <c>checkpoints</c> table held,
    /// in its historical order (stage, then id, ordinal).</summary>
    public IReadOnlyList<TaskItem> Checkpoints() =>
        _tasks.Where(t => t.Kind == WorkItemKinds.Checkpoint)
              .OrderBy(t => t.StageId, StringComparer.Ordinal)
              .ThenBy(t => t.TaskId, StringComparer.Ordinal).ToList();

    public IReadOnlyList<TaskItem> ForCheckpoint(string checkpointId) =>
        _tasks.Where(t => t.CheckpointId.Equals(checkpointId, StringComparison.Ordinal))
              .OrderBy(t => t.Order).ToList();

    /// <summary>PF3: the union of declared paths on a checkpoint's OPEN cards (todo/in_progress) —
    /// what <c>ReadyItem.PathClaims</c> carries into the assignment policy. Done/skipped cards no
    /// longer claim anything. null = no open card declares a path (no detectable conflict).</summary>
    public IReadOnlyList<string>? DeclaredOpenPaths(string checkpointId)
    {
        List<string>? paths = null;
        foreach (var t in _tasks)
        {
            if (t.CheckpointId != checkpointId || t.Status is not ("todo" or "in_progress")) continue;
            foreach (var p in t.Paths)
            {
                paths ??= new List<string>();
                if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
            }
        }
        return paths;
    }

    /// <summary>Current sub-task (first non-done, non-skipped) or null.</summary>
    public TaskItem? CurrentTask(string checkpointId)
    {
        foreach (var t in _tasks.OrderBy(t => t.Order))
        {
            if (t.CheckpointId == checkpointId && t.Status is "todo" or "in_progress")
                return t;
        }
        return null;
    }

    private static bool IsValidTransition(string from, string to) => (from, to) switch
    {
        // W1.1: same → same is a legal metadata refresh (a repeated done-claim updates its
        // commit/evidence; seed re-asserts are no-ops) — never a state change.
        (var f, var t) when f == t => true,
        ("todo", "in_progress") => true,
        ("in_progress", "done") => true,
        ("in_progress", "todo") => true,
        ("todo", "done") => true,
        ("todo", "skipped") => true,
        ("in_progress", "skipped") => true,
        // G2: a card on the Kanban board can be pulled back — reopening a done/skipped task makes it
        // current again (CurrentTask picks it up), which is the point of moving it left.
        ("done", "in_progress") => true,
        ("done", "todo") => true,
        ("skipped", "todo") => true,
        ("skipped", "in_progress") => true,
        // W1.1: tracker BLOCKED lives in the graph now — in/out of the open states, and straight to
        // done when the block resolves with the work already delivered.
        ("todo", "blocked") => true,
        ("in_progress", "blocked") => true,
        ("blocked", "todo") => true,
        ("blocked", "in_progress") => true,
        ("blocked", "done") => true,
        _ => false,
    };
}
