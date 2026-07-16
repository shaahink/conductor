namespace Conductor.Core.Http;

public sealed record TaskDto(string TaskId, string CheckpointId, string Title, string Status, string Source, int Order,
    // P3: the owner-editable per-task extra context (empty = none).
    string Context,
    // PF3: the card's declared repo-relative paths (empty = no declared claims).
    IReadOnlyList<string> Paths);

public sealed record TasksDto(IReadOnlyList<TaskDto> Tasks);
