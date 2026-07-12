namespace Conductor.Core.Http;

public sealed record TaskDto(string TaskId, string CheckpointId, string Title, string Status, string Source, int Order);

public sealed record TasksDto(IReadOnlyList<TaskDto> Tasks);
