namespace Conductor.Core.Http;

/// <summary>P3: one labeled building block of a task's prompt (GET /prompt/blocks?task=). Editable
/// marks the task-scoped blocks (title, extra context) the Face lets the owner edit — as structured
/// task data via POST /tasks/edit, never as raw prompt text.</summary>
public sealed record PromptBlockDto(string Kind, string Label, string Content, bool Editable);

public sealed record PromptBlocksDto(bool Ok, string? Error, string TaskId, string CheckpointId, string StageId,
    IReadOnlyList<PromptBlockDto> Blocks);
