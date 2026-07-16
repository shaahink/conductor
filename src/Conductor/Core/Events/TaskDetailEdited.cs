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
}
