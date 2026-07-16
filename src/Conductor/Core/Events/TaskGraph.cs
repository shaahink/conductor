using Conductor.Models;

namespace Conductor.Core.Events;

/// <summary>
/// B9.1: folds <see cref="TaskAdded"/> and <see cref="TaskStatusChanged"/> events into a live task
/// graph ordered by checkpoint → task order. Pure projection — replay a log segment from any point.
/// Allowed status transitions: todo → in_progress → done (also todo → skipped), plus the G2 reopen
/// moves back out of done/skipped so the Kanban board can pull a card left.
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
                    var item = new TaskItem
                    {
                        TaskId = ta.TaskId,
                        CheckpointId = ta.CheckpointId,
                        Title = ta.Title,
                        Status = "todo",
                        Source = ta.Source,
                        Order = ta.Order,
                    };
                    _tasks.Add(item);
                    _byId[ta.TaskId] = item;
                    break;

                case TaskStatusChanged sc:
                    if (_byId.TryGetValue(sc.TaskId, out var existing))
                    {
                        if (IsValidTransition(existing.Status, sc.Status))
                            existing.Status = sc.Status;
                    }
                    break;

                case TaskDetailEdited de:
                    if (_byId.TryGetValue(de.TaskId, out var edited))
                    {
                        // null = unchanged; a blank title is refused at write time (TaskWrites), so a
                        // replayed log can never blank a card. Context empty = cleared, by design.
                        if (!string.IsNullOrWhiteSpace(de.Title)) edited.Title = de.Title;
                        if (de.Context != null) edited.Context = de.Context;
                    }
                    break;
            }
        }
    }

    public TaskItem? Find(string taskId) =>
        _byId.TryGetValue(taskId, out var t) ? t : null;

    public IReadOnlyList<TaskItem> ForCheckpoint(string checkpointId) =>
        _tasks.Where(t => t.CheckpointId.Equals(checkpointId, StringComparison.Ordinal))
              .OrderBy(t => t.Order).ToList();

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
        _ => false,
    };
}
