namespace Conductor.Core.Http;

/// <summary>G2.1: task writes over the control plane. <c>POST /tasks/update</c> moves a card
/// (todo / in_progress / done / skipped); <c>POST /tasks/add</c> creates one under a checkpoint.
/// Both emit the same events the MCP task tools do (<see cref="Conductor.Core.Events.TaskWrites"/>),
/// so the Kanban board and the agent write the very same task graph.</summary>
public sealed record TaskUpdateRequestDto(string? TaskId, string? Status);

/// <summary>Order 0 (or absent) means "append after the checkpoint's last task".</summary>
public sealed record TaskAddRequestDto(string? CheckpointId, string? Title, int Order);

/// <summary>Result of either task write. <c>Status</c> echoes the task's <b>actual</b> post-fold
/// status — an illegal transition is a recorded no-op (same contract as the MCP tools), so the
/// caller re-renders from what really happened, not from what it asked for.</summary>
public sealed record TaskWriteResultDto(
    bool Ok, string? Error, string? TaskId, string? Status, string? CheckpointId, string? Title, int Order);
