using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Models;

namespace Conductor.Core;

public sealed partial class Orchestrator
{
    private async Task RunPhaseGateAsync(PendingPhaseGate pg, CancellationToken ct)
    {
        var head = Git.Head(plan.Repo);
        var sig = GateRunner.BatterySignature(plan, head, pg.StageId);
        IReadOnlyList<GateResult> gates;
        bool green;

        if (sig == state.LastGreenGateSig)
        {
            Log($"phase gate {pg.StageId}: tree unchanged since last green battery ({Short(head)}) — reusing result, skipping rerun");
            green = true;
            gates = _lastGates ?? Array.Empty<GateResult>();
        }
        else
        {
            state.Status = RunStatus.VerifyingGates;
            Save();
            PushIdleSnapshot();
            Log($"phase gate {pg.StageId}: running FULL battery at {Short(head)} to confirm the phase");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            gates = await RunGateBatteryAsync(ct, fastOnly: false).ConfigureAwait(false);
            _lastGates = gates;

            if (ct.IsCancellationRequested)
            {
                state.Status = RunStatus.Idle;
                Log("phase gate interrupted — will re-run on resume");
                Save();
                return;
            }
            green = GateRunner.AllRequiredPassed(gates);
            EmitGates(gates, "phase");
            _runOverheadUsd += gates.Sum(g => g.EstimatedCostUsd(plan.Limits.OverheadCostPerSecond));
            state.PerRunOverheadCostUsd = _runOverheadUsd;
            Log($"phase gate {pg.StageId} finished in {sw.Elapsed.TotalSeconds:0}s — {(green ? "GREEN" : "RED")}: {GateRunner.Summary(gates)}", green ? "pass" : "fail");
            if (green) state.LastGreenGateSig = sig;
        }

        if (green)
        {
            if (plan.Audit is { Enabled: true, EnableParallel: true } && !state.AuditedStages.Contains(pg.StageId)
                && HasNextUnconfirmedStage(pg.StageId))
            {
                state.PendingParallelAudit = new PendingParallelAudit { StageId = pg.StageId, StageStartHead = pg.StageStartHead };
                state.PendingAudit = null;
                state.PendingPhaseGate = null;
                state.AuditedStages.Add(pg.StageId);
                Log($"phase {pg.StageId} full battery GREEN — confirming now, audit will run in parallel with next deliver");
                await ConfirmStageAsync(pg.StageId, ct).ConfigureAwait(false);
                return;
            }

            if (plan.Audit is { Enabled: true } && !state.AuditedStages.Contains(pg.StageId))
            {
                state.PendingAudit = new PendingAudit { StageId = pg.StageId, StageStartHead = pg.StageStartHead };
                state.PendingPhaseGate = null;
                state.Status = RunStatus.Idle;
                Log($"phase {pg.StageId} full battery GREEN — queuing auto-fix audit session");
                SaveAndReport();
            }
            else
            {
                await ConfirmStageAsync(pg.StageId, ct).ConfigureAwait(false);
            }
        }
        else
        {
            state.AttemptsThisStage++;
            state.PendingFix = new PendingFix
            {
                FromSession = state.History.LastOrDefault()?.Number ?? 0,
                GateFailures = GateRunner.FailureDetails(gates),
                ProgressSummary = $"phase {pg.StageId} full battery is RED although its checkpoints read DONE — make the claims true",
            };
            state.PendingPhaseGate = null;
            state.Status = RunStatus.Idle;
            Log($"phase {pg.StageId} full battery RED — queuing fix session (attempt {state.AttemptsThisStage}/{MaxAttempts(CurrentStageConfig())})");
            SaveAndReport();
        }
    }

    private void ScheduleGateOrAudit(string stageId, string startHead)
    {
        Gates.ScheduleGateOrAudit(stageId, startHead, Log, HasNextUnconfirmedStage);
    }

    private void SquashBookkeeping(string stageId)
    {
        if (state.SquashedStages.Contains(stageId)) return;
        if (!state.StageStartHeads.TryGetValue(stageId, out var startHead) || string.IsNullOrWhiteSpace(startHead))
        {
            Log($"P4 squash: no start-head recorded for stage {stageId} — skipping");
            return;
        }

        state.SquashedStages.Add(stageId);
        Save();
        Log($"P4 squash: collapsing chore(conductor): commits for stage {stageId} since {Short(startHead)}");

        try
        {
            var ok = Git.SquashChoreCommits(plan.Repo, startHead);
            if (ok)
                Log($"P4 squash: stage {stageId} complete — chore(conductor): commits squashed");
            else
                Log($"P4 squash: git rebase returned non-zero for stage {stageId} — history unchanged", "warn");
        }
        catch (Exception ex)
        {
            Log($"P4 squash: failed for stage {stageId}: {ex.Message} — history unchanged", "warn");
            state.SquashedStages.Remove(stageId);
        }
    }

    private async Task ConfirmStageAsync(string id, CancellationToken ct)
    {
        var stage = plan.Stages.FirstOrDefault(s => s.Id == id);
        if (stage is { OwnerGate: true } && !state.OwnerApprovedStages.Contains(id))
        {
            events.Emit(new OwnerApprovalRequested { StageId = id });
            state.Status = RunStatus.AwaitingOwner;
            state.AwaitingOwnerReason = AwaitingOwnerReason.OwnerGate;
            Log($"owner-gate: stage {id} green — awaiting owner approval (run `conductor approve` or press R in the TUI)");
            SaveAndReport();
            Notify($"Conductor {plan.Name}: stage {id} is green and awaiting owner approval");
            _ = telegram.PushWithKeyboardAsync($"Stage {id} green — owner approval needed",
                [("Approve", "approve")], CancellationToken.None);
            return;
        }
        if (!state.ConfirmedStages.Contains(id)) state.ConfirmedStages.Add(id);
        state.AwaitingOwnerReason = null;
        state.PendingPhaseGate = null;
        state.PendingAudit = null;
        state.PendingFix = null;
        state.AttemptsThisStage = 0;
        if (stage?.PostHook is { } postHook)
            await RunStageHookAsync(id, "post-hook", postHook, ct).ConfigureAwait(false);
        await Lanes.RunFollowupFixLanesAsync(id, ct).ConfigureAwait(false);
        if (state.PauseAfterStage)
        {
            state.PauseAfterStage = false;
            state.Status = RunStatus.Paused;
            Log($"✓ phase {id} CONFIRMED — parked (pause-after-stage was set)");
        }
        else
        {
            state.Status = RunStatus.Idle;
            Log($"✓ phase {id} CONFIRMED (full battery green{(state.AuditedStages.Contains(id) ? " + audit" : "")}) — advancing");
        }
        events.Emit(new StageConfirmed { StageId = id, Audited = state.AuditedStages.Contains(id) });
        _runDb?.ConfirmStage(state.RunId, id);
        SquashBookkeeping(id);
        SaveAndReport();
    }

    private bool HasNextUnconfirmedStage(string stageId)
    {
        var idx = plan.Stages.FindIndex(s => s.Id == stageId);
        if (idx < 0) return false;
        for (var i = idx + 1; i < plan.Stages.Count; i++)
        {
            var sid = plan.Stages[i].Id;
            if (!state.SkippedStages.Contains(sid) && !state.ConfirmedStages.Contains(sid))
                return true;
        }
        return false;
    }

    private async Task ApproveAwaitingOwnerAsync(CancellationToken ct)
    {
        var stageId = state.CurrentStage
            ?? plan.Stages.FirstOrDefault(s => !state.ConfirmedStages.Contains(s.Id) && !state.SkippedStages.Contains(s.Id))?.Id;
        switch (OwnerApproval.Decide(state.AwaitingOwnerReason))
        {
            case ApprovalOutcome.ResumeSession:
                _sessionApproved = true;
                state.AwaitingOwnerReason = null;
                state.Status = RunStatus.Idle;
                if (stageId != null) events.Emit(new OwnerApprovalGranted { StageId = stageId });
                Save();
                Log("owner approved (approval mode) — running the next session");
                break;
            case ApprovalOutcome.ResetBudgetAndResume:
                _runCostUsd = 0;
                _runTokens = 0;
                _runOverheadUsd = 0;
                state.PerRunCostUsd = 0;
                state.PerRunTokens = 0;
                state.PerRunOverheadCostUsd = 0;
                state.AwaitingOwnerReason = null;
                state.Status = RunStatus.Idle;
                if (stageId != null) events.Emit(new OwnerApprovalGranted { StageId = stageId });
                Save();
                Log("owner approved (budget) — budget window reset, continuing");
                break;
            default:
                if (stageId == null) { state.Status = RunStatus.Idle; Save(); break; }
                if (!state.OwnerApprovedStages.Contains(stageId))
                {
                    events.Emit(new OwnerApprovalGranted { StageId = stageId });
                    state.OwnerApprovedStages.Add(stageId);
                    Log($"owner approved stage {stageId} — continuing");
                }
                await ConfirmStageAsync(stageId, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunStageHookAsync(string stageId, string label, HookConfig hook, CancellationToken ct)
    {
        Log($"{label}: {stageId} — {hook.Command}");
        var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? plan.Repo : Path.Combine(plan.Repo, hook.Cwd);
        var r = await ProcessRunner.RunPowerShellAsync(hook.Command, cwd, TimeSpan.FromMinutes(hook.TimeoutMinutes), ct).ConfigureAwait(false);
        var timedOut = r.TimedOut ? " (timed out)" : "";
        Log($"{label}: exit {r.ExitCode}{timedOut} in {r.Duration.TotalSeconds:0}s");
        if (r.ExitCode != 0)
        {
            var outputSnippet = r.Output.Length > 500 ? r.Output[..500] + "\n…(truncated)" : r.Output;
            var detail = $"stage {stageId} {label} failed (exit {r.ExitCode}): {hook.Command}";
            Log($"ERROR: {detail}\n{outputSnippet.TrimEnd()}");
            if (label == "pre-hook")
                NeedsHuman(detail);
        }
        else if (label == "pre-hook")
        {
            state.PreHookRunStages.Add(stageId);
        }
    }
}
