namespace Conductor.Core.Events;

/// <summary>P3: an owner edited a task's own data — its title and/or its extra-context block.
/// A null field means "unchanged"; an empty <see cref="Context"/> clears it.</summary>
public sealed record TaskDetailEdited : ConductorEvent
{
    public required string TaskId { get; init; }
    public string? Title { get; init; }
    public string? Context { get; init; }
}
