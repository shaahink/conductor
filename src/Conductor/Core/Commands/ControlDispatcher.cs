using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Commands;

/// <summary>
/// Executes the 11 control verbs (pause/resume/abort/skip/kill/stop-after/retry-stage/rollback/
/// pause-after-stage/goto/heartbeat) against run state. This is the single place verb behavior lives —
/// every ingress (the TUI's in-process queue, the file-based control.json written by CLI verbs,
/// and the F5 HTTP control-plane POST) converges on the same <see cref="ControlCommand"/> shape
/// and calls <see cref="DispatchAsync"/>, so none of them can drift from what a verb actually does.
/// </summary>
/// <remarks>
/// Extracted from Orchestrator.HandleControlAsync's inline switch (F5 prep). Orchestrator still
/// owns the polling loop (when a command is checked, interleaved with the run loop) and file I/O
/// (control.json read/delete) — this class owns only what each verb does once one arrives.
/// </remarks>
public sealed class ControlDispatcher(
    PlanConfig plan,
    RunState state,
    IProgressSink sink,
    IEventSink events,
    Action<string> log,
    Action save,
    Action deleteControlFile,
    Action<StageConfig, string> skipStage,
    Func<CancellationToken, Task> approveAwaitingOwner)
{
    // Skip/pause requested mid-session can't take effect until the agent finishes — the run loop
    // consumes these once the current session ends via the Consume* methods below.
    private bool _pendingSkip;
    private bool _pausePending;

    public bool ConsumePendingSkip()
    {
        if (!_pendingSkip) return false;
        _pendingSkip = false;
        return true;
    }

    public bool ConsumePausePending()
    {
        if (!_pausePending) return false;
        _pausePending = false;
        return true;
    }

    public async Task<ControlAction?> DispatchAsync(ControlCommand cmd, bool inSession, CancellationToken ct)
    {
        var action = cmd.Action;
        if (action == null) return null;
        log($"control: {action}{(inSession ? " (during session)" : "")}");
        switch (action)
        {
            case ControlAction.PauseAfterSession:
                if (inSession) _pausePending = true;
                else { state.Status = RunStatus.Paused; save(); }
                sink.Toast(new ToastMessage($"pause-after-session {(inSession ? "queued" : "applied")}", LogSeverity.Success));
                deleteControlFile();
                break;
            case ControlAction.StopAfterSession:
                state.StopAfterSession = true;
                sink.Toast(new ToastMessage("stop-after-session: will stop when current session ends", LogSeverity.Success));
                deleteControlFile();
                break;
            case ControlAction.Heartbeat:
                // Benign and takes effect *during* the session (it needs the live agent to snapshot),
                // so unlike the other verbs it must NOT go through the "re-run after session" guard
                // below — the run loop does the actual RefreshReport when this action bubbles back up.
                sink.Toast(inSession
                    ? new ToastMessage("heartbeat: refreshing report", LogSeverity.Success)
                    : new ToastMessage("heartbeat: no active session to snapshot", LogSeverity.Info));
                deleteControlFile();
                break;
            case ControlAction.ResumeRun:
                if (state.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
                {
                    if (state.Status == RunStatus.AwaitingOwner)
                    {
                        await approveAwaitingOwner(ct).ConfigureAwait(false);
                        deleteControlFile();
                        break;
                    }
                    state.Status = RunStatus.Idle;
                    state.AttentionReason = null;
                    save();
                    log("resumed by user");
                    sink.Toast(new ToastMessage("run resumed", LogSeverity.Success));
                    deleteControlFile();
                }
                break;
            case ControlAction.SkipStage:
                if (inSession) _pendingSkip = true;
                else if (state.CurrentStage != null)
                {
                    var s = plan.Stages.FirstOrDefault(x => x.Id == state.CurrentStage);
                    if (s != null) { skipStage(s, "skipped by user control"); sink.Toast(new ToastMessage($"stage {state.CurrentStage} skipped", LogSeverity.Success)); }
                }
                deleteControlFile();
                break;
            case ControlAction.AbortNow when !inSession:
                state.Status = RunStatus.Aborted;
                save();
                sink.Toast(new ToastMessage("run aborted by user", LogSeverity.Warn));
                deleteControlFile();
                break;
            case ControlAction.RetryStage when !inSession:
                state.PendingFix = null;
                state.PendingResume = null;
                state.AttemptsThisStage = 0;
                state.Status = RunStatus.Idle;
                save();
                log($"retry: stage {state.CurrentStage} — attempt counter reset, re-queuing");
                sink.Toast(new ToastMessage($"retry: stage {state.CurrentStage} re-queued", LogSeverity.Success));
                deleteControlFile();
                break;
            case ControlAction.Rollback when !inSession:
                var force = cmd.Force;
                if (state.CurrentStageStartHead is not { Length: > 0 } sha)
                {
                    log("rollback refused: no checkpoint commit recorded for current stage");
                    sink.Toast(new ToastMessage("rollback refused: no commit for current stage", LogSeverity.Error));
                    break;
                }
                if (!force && Git.IsDirty(plan.Repo))
                {
                    log($"rollback refused: working tree is dirty — {Git.DirtySummary(plan.Repo)}. Re-run with --force to discard and reset.");
                    sink.Toast(new ToastMessage("rollback refused: dirty working tree", LogSeverity.Error));
                    break;
                }
                var fromSha = Git.Head(plan.Repo);
                log($"rollback: resetting to {Short(sha)} (stage {state.CurrentStage} start){(force && Git.IsDirty(plan.Repo) ? " — discarding dirty working tree (--force)" : "")}");
                Git.Exec(plan.Repo, "reset", "--hard", sha);
                events.Emit(new RollbackExecuted { StageId = state.CurrentStage ?? "?", FromSha = fromSha, ToSha = sha, Forced = force });
                state.Status = RunStatus.Idle;
                save();
                sink.Toast(new ToastMessage($"rollback: reset to {Short(sha)}", LogSeverity.Success));
                deleteControlFile();
                break;
            case ControlAction.PauseAfterStage when !inSession:
                state.PauseAfterStage = true;
                state.Status = RunStatus.Idle;
                save();
                log($"pause-after-stage: will park when {state.CurrentStage} completes");
                sink.Toast(new ToastMessage($"pause-after-stage: will park after {state.CurrentStage}", LogSeverity.Success));
                deleteControlFile();
                break;
            case ControlAction.Goto when !inSession:
                if (cmd.StageId == null) { log("goto: no target stage — use `conductor goto <stage>`"); sink.Toast(new ToastMessage("goto: no target stage", LogSeverity.Error)); break; }
                {
                    var tg = cmd.StageId;
                    var target = plan.Stages.FirstOrDefault(s => s.Id == tg);
                    if (target == null) { log($"goto refused: stage '{tg}' not found in plan"); sink.Toast(new ToastMessage($"goto refused: stage '{tg}' not found", LogSeverity.Error)); break; }
                    if (state.SkippedStages.Contains(tg)) { log($"goto refused: stage '{tg}' is skipped"); sink.Toast(new ToastMessage($"goto refused: stage '{tg}' is skipped", LogSeverity.Error)); break; }
                    // A goto to an already-confirmed stage must actually take effect: un-confirm it (and
                    // drop any owner approval) so SelectStage re-runs it instead of silently skipping.
                    state.ConfirmedStages.Remove(tg);
                    state.OwnerApprovedStages.Remove(tg);
                    state.AwaitingOwnerReason = null;
                    state.CurrentStage = tg;
                    state.CurrentStageStartHead = Git.Head(plan.Repo);
                    state.AttemptsThisStage = 0;
                    state.PendingFix = null;
                    state.PendingResume = null;
                    state.PendingPhaseGate = null;
                    state.PendingAudit = null;
                    state.Status = RunStatus.Idle;
                    save();
                    log($"goto: jumped to stage {tg} {target.Title}");
                    sink.Toast(new ToastMessage($"goto: jumped to {tg} {target.Title}", LogSeverity.Success));
                    deleteControlFile();
                }
                break;
        }
        if (inSession && action is ControlAction.RetryStage or ControlAction.Rollback or ControlAction.PauseAfterStage or ControlAction.Goto or ControlAction.AbortNow)
            log($"control: {action} received mid-session — re-run after session ends for it to take effect");
        return action;
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;
}
