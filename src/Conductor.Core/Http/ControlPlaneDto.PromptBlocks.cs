namespace Conductor.Core.Http;

/// <summary>P3: one labeled building block of a task's prompt (GET /prompt/blocks?task=). Editable
/// marks the task-scoped blocks (title, extra context) the Face lets the owner edit — as structured
/// task data via POST /tasks/edit, never as raw prompt text.</summary>
public sealed record PromptBlockDto(string Kind, string Label, string Content, bool Editable);

/// <summary>W2.3: <c>PromptSection</c> is the card's task-scoped blocks rendered exactly as the
/// session prompt on disk carries them (<see cref="Conductor.Planning.PromptBlockRenderer"/>) — the
/// literal answer to "what will the agent receive for this card?". Additive: existing consumers that
/// only read <c>Blocks</c> are unaffected.</summary>
public sealed record PromptBlocksDto(bool Ok, string? Error, string TaskId, string CheckpointId, string StageId,
    IReadOnlyList<PromptBlockDto> Blocks, string PromptSection = "");
