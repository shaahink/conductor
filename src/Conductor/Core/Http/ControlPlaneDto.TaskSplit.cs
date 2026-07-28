namespace Conductor.Core.Http;

/// <summary>W4.3: ask the plan's advisor to break one card into children (<c>POST /tasks/split</c>).
/// <c>Instruction</c> is the owner's optional steer ("split by layer", "one per endpoint");
/// <c>Count</c> optionally pins how many. Proposal only — nothing mutates here.</summary>
public sealed record TaskSplitRequestDto(string? TaskId, string? Instruction = null, int? Count = null);

/// <summary>One proposed child. Confirmed by posting it to <c>/tasks/add</c> under the parent's
/// checkpoint — the same propose→confirm contract as refine and plan import.</summary>
public sealed record TaskSplitChildDto(string Title, string? Context);

public sealed record TaskSplitResultDto(
    bool Ok, string? Error, string? TaskId, string? CheckpointId,
    IReadOnlyList<TaskSplitChildDto> Subtasks, string? Interpreter);
