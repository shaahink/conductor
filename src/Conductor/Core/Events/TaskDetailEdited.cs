namespace Conductor.Core.Events;

/// <summary>P3: an owner edited a task's own data — its title, its extra-context block, and/or its
/// declared paths (PF3). A null field means "unchanged"; an empty <see cref="Context"/> clears it;
/// an empty <see cref="Paths"/> clears the declared claims.</summary>
public sealed record TaskDetailEdited : ConductorEvent
{
    public required string TaskId { get; init; }
    public string? Title { get; init; }
    public string? Context { get; init; }
    public string[]? Paths { get; init; }

    /// <summary>W4.4: this item's QA override — <c>inherit</c> | <c>verify</c> | <c>off</c>.
    /// null = unchanged; <c>inherit</c> clears the override and the stage/plan dial decides again.
    /// The dial existed at plan and stage level (P2) and could not reach the individual work item,
    /// which is criterion 5: "deliver these one-by-one, but verify THAT one".</summary>
    public string? Qa { get; init; }
}
