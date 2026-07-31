using Conductor.Core.Events;
using Conductor.Core.Store;

namespace Conductor.Commands;

/// <summary>
/// SC5.3: the board writes behind <c>conductor task</c>, as testable logic rather than console code.
/// <para>Every move here goes through <see cref="IRunStore.ApplyTaskStatus"/> — i.e. through
/// <see cref="TaskWrites"/>, the same validator <c>POST /tasks/update</c> and the MCP
/// <c>task_update</c> tool use — and every answer is built from the card's POST-FOLD status. The CLI
/// used to own a private write path that printed success unconditionally: a transition the fold
/// refused (round-four #1) left the operator believing the card had moved, and a mis-drag could only
/// be undone with a hand-rolled HTTP POST because the CLI had no way to say "todo" at all.</para>
/// </summary>
public static class TaskBoard
{
    /// <summary>Move a card and report what actually happened. <paramref name="status"/> is graph
    /// vocabulary (todo | in_progress | blocked | done | skipped).</summary>
    public static TaskBoardResult Move(IRunStore store, string runId, string taskId, string status,
        string? commit = null, string? evidence = null, string source = "agent")
    {
        ArgumentNullException.ThrowIfNull(store);
        var (actual, error) = store.ApplyTaskStatus(runId, taskId, status, commit, evidence, source);
        if (actual is null)
            return new TaskBoardResult(false, taskId, "", $"refused: {error}");

        return string.Equals(actual, status, StringComparison.Ordinal)
            ? new TaskBoardResult(true, taskId, actual, $"checkpoint {taskId} → {TaskWrites.Label(actual)}")
            : new TaskBoardResult(false, taskId, actual,
                $"refused: {taskId} is {TaskWrites.Label(actual)} and stayed {TaskWrites.Label(actual)} — "
                + $"{TaskWrites.Label(actual)} → {TaskWrites.Label(status)} is not a legal move");
    }

    /// <summary>The narrower <c>--in-progress</c>: TODO only, so an agent cannot un-claim a checkpoint
    /// by fat-fingering an id. The refusal is now REPORTED — the whole point of SC5.3 — with the verb
    /// that would do what the caller meant.</summary>
    public static TaskBoardResult Start(IRunStore store, string runId, string taskId, string source = "agent")
    {
        ArgumentNullException.ThrowIfNull(store);
        var actual = store.MarkCheckpointInProgress(runId, taskId, source);
        if (string.IsNullOrEmpty(actual))
            return new TaskBoardResult(false, taskId, "", $"refused: task not found: {taskId}");
        if (string.Equals(actual, "in_progress", StringComparison.Ordinal))
            return new TaskBoardResult(true, taskId, actual, $"checkpoint {taskId} → IN PROGRESS");

        return new TaskBoardResult(false, taskId, actual,
            $"refused: {taskId} is {TaskWrites.Label(actual)} and stayed {TaskWrites.Label(actual)} — "
            + "--in-progress starts a TODO checkpoint and will not reopen a claimed one; "
            + $"use `conductor task --todo {taskId}` if you really mean to reopen it");
    }

    /// <summary>Record an acceptance correction against a card: appended to the card's context (which
    /// the next session's composed prompt carries) and written to the knowledge ledger, the pairing
    /// <c>--blocked-until</c> makes — a correction nobody reads is not a correction.</summary>
    public static TaskBoardResult Amend(IRunStore store, string runId, string taskId, string note, string? stageId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var (context, error) = store.AmendTask(runId, taskId, note);
        if (context is null)
            return new TaskBoardResult(false, taskId, "", $"refused: {error}");

        store.WriteLedger(runId, null, stageId, "amendment", $"{taskId}: {note.Trim()}");
        return new TaskBoardResult(true, taskId, context,
            $"checkpoint {taskId} amended — the correction rides the card into the next session's prompt");
    }
}

/// <summary>One board write's outcome. <c>Actual</c> is the POST-FOLD truth: the card's real status
/// after a move, its full context after an amendment. <c>Ok</c> false means the board did not change
/// the way the caller asked — the CLI exits non-zero on it, so a script cannot read a refusal as
/// success either.</summary>
public sealed record TaskBoardResult(bool Ok, string TaskId, string Actual, string Message);
