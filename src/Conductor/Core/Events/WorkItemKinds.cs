namespace Conductor.Core.Events;

/// <summary>W1.1: the unified work-graph vocabulary. A work item is either a checkpoint (the
/// verified contract rows the engine schedules) or a subtask (an advisory break-point beneath
/// one). Provenance rides the event family's <c>Source</c> field: plan | tracker | import |
/// human | agent.</summary>
public static class WorkItemKinds
{
    public const string Checkpoint = "checkpoint";
    public const string Subtask = "subtask";
}
