namespace Conductor.Core.Events;

public sealed record TaskAdded : ConductorEvent
{
    public required string TaskId { get; init; }
    public required string CheckpointId { get; init; }
    public required string Title { get; init; }
    public required string Source { get; init; }
    public int Order { get; init; }
}

public sealed record TaskStatusChanged : ConductorEvent
{
    public required string TaskId { get; init; }
    public required string Status { get; init; }
}

public sealed record NoteAdded : ConductorEvent
{
    public required string Kind { get; init; }
    public required string Content { get; init; }
    public string? StageId { get; init; }
}
