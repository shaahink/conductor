using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class VerdictEngine
{
    // ── SessionRunner delegate entry point (public) ──

    public void QueueResume(SessionRecord rec, string reason, bool countResume = true, bool force = false)
    {
        _ctx.State.PendingResume = new PendingResume
        {
            FromSession = rec.Number,
            ClaudeSessionId = rec.ClaudeSessionId,
            Reason = reason,
            ResumeCount = rec.ResumeCount + (countResume ? 1 : 0),
        };
        if (force) _ctx.State.PendingResume.ResumeCount = Math.Min(_ctx.State.PendingResume.ResumeCount, _ctx.Plan.Limits.MaxResumesPerSession - 1);
    }

    // ── main-loop entry points (internal) ──

    internal async Task RunPhaseGateAsync(PendingPhaseGate pg, CancellationToken ct)
    {
        var head = Git.Head(_ctx.Plan.Repo);
        var sig = GateRunner.BatterySignature(_ctx.Plan, head, pg.StageId);
        var configured = GateRunner.ConfiguredForStage(_ctx.Plan, StageConfigFor(pg.StageId));
        IReadOnlyList<GateResult> gates;
        bool green;
        var reused = false;

        if (sig == _ctx.State.LastGreenGateSig)
        {
            reused = true;
            green = true;
            gates = _ctx.LastGates ?? Array.Empty<GateResult>();
            // SC2.2: the reuse path carried no gate token at all, so a log consumer grepping the canonical
            // vocabulary saw a stage confirm with no battery verdict anywhere in the run. The signature
            // match IS a green battery for this exact tree and gate set, so a restart that lost the
            // in-memory results still reports GREEN rather than mistaking itself for a gateless stage.
            var reusedToken = gates.Count > 0 ? GateRunner.Token(gates) : configured > 0 ? "gates GREEN" : "gates NONE";
            _ctx.Log($"phase gate {pg.StageId} finished in 0s — {reusedToken}: tree unchanged since last green battery ({Short(head)}) — reusing result, skipping rerun", "pass");
        }
        else
        {
            _ctx.State.Status = RunStatus.VerifyingGates;
            _ctx.Save();
            _pushIdleSnapshot();
            _ctx.Log($"phase gate {pg.StageId}: running FULL battery at {Short(head)} to confirm the phase");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            gates = await RunGateBatteryAsync(ct, fastOnly: false).ConfigureAwait(false);
            _ctx.LastGates = gates;

            if (ct.IsCancellationRequested)
            {
                _ctx.State.Status = RunStatus.Idle;
                _ctx.Log("phase gate interrupted — will re-run on resume");
                _ctx.Save();
                return;
            }
            green = GateRunner.AllRequiredPassed(gates);
            EmitGates(gates, "phase");
            _ctx.RunOverheadUsd += gates.Sum(g => g.EstimatedCostUsd(_ctx.Plan.Limits.OverheadCostPerSecond));
            _ctx.State.PerRunOverheadCostUsd = _ctx.RunOverheadUsd;
            _ctx.Log($"phase gate {pg.StageId} finished in {sw.Elapsed.TotalSeconds:0}s — {GateRunner.Token(gates)}: {GateRunner.Summary(gates)}", green ? "pass" : "fail");
            if (green) _ctx.State.LastGreenGateSig = sig;
        }

        // SC2.2: what this confirmation will actually rest on, decided ONCE here where the plan's
        // stage-scoped gate set and the battery result are both in hand — not re-guessed by a constant
        // string at the confirmation site.
        var basis = GateRunner.ConfirmationBasis(configured, gates, reused);

        if (green)
        {
            if (_ctx.Plan.Audit is { Enabled: true, EnableParallel: true }
                && !_ctx.State.AuditedStages.Contains(pg.StageId) && HasNextUnconfirmedStage(pg.StageId))
            {
                _ctx.State.PendingParallelAudit = new PendingParallelAudit { StageId = pg.StageId, StageStartHead = pg.StageStartHead };
                _ctx.State.PendingAudit = null;
                _ctx.State.PendingPhaseGate = null;
                _ctx.State.AuditedStages.Add(pg.StageId);
                _ctx.Log($"phase {pg.StageId} full battery — {basis} — confirming now, audit will run in parallel with next deliver");
                await ConfirmStageAsync(pg.StageId, ct, basis).ConfigureAwait(false);
                return;
            }
            if (_ctx.Plan.Audit is { Enabled: true } && !_ctx.State.AuditedStages.Contains(pg.StageId))
            {
                _ctx.State.PendingAudit = new PendingAudit { StageId = pg.StageId, StageStartHead = pg.StageStartHead };
                _ctx.State.PendingPhaseGate = null;
                _ctx.State.Status = RunStatus.Idle;
                _ctx.Log($"phase {pg.StageId} full battery — {basis} — queuing auto-fix audit session");
                _saveAndReport();
            }
            else
            {
                await ConfirmStageAsync(pg.StageId, ct, basis).ConfigureAwait(false);
            }
        }
        else
        {
            _ctx.State.AttemptsThisStage++;
            _ctx.State.PendingFix = new PendingFix
            {
                FromSession = _ctx.State.History.LastOrDefault()?.Number ?? 0,
                GateFailures = GateRunner.FailureDetails(gates),
                ProgressSummary = $"phase {pg.StageId} full battery — {GateRunner.Token(gates)} — make the claims true",
            };
            _ctx.State.PendingPhaseGate = null;
            _ctx.State.Status = RunStatus.Idle;
            // SC2.2: NextAttemptNumber, not AttemptsThisStage — this line names the session it is queuing,
            // and that session announces itself with the same number (devcontext #19).
            _ctx.Log($"phase {pg.StageId} full battery — {GateRunner.Token(gates)} — queuing fix session (attempt {_ctx.State.NextAttemptNumber}/{MaxAttempts(CurrentStageConfig())})", "fail");
            _saveAndReport();
        }
    }

    internal void ScheduleGateOrAudit(string stageId, string startHead)
    {
        _gates.ScheduleGateOrAudit(stageId, startHead, _ctx.Log, HasNextUnconfirmedStage);
    }

    /// <param name="basis">SC2.2: what this confirmation rests on, in <see cref="GateRunner.ConfirmationBasis"/>'s
    /// vocabulary. Callers that ran the battery pass what they measured; the owner-approval path has no
    /// battery in hand and falls back to the plan's stage-scoped gate set plus the last results, which is
    /// still measured — never the old constant "(full battery green)" that nine gateless stages printed.</param>
    internal async Task ConfirmStageAsync(string id, CancellationToken ct, string? basis = null)
    {
        var stage = _ctx.Plan.Stages.FirstOrDefault(s => s.Id == id);
        if (stage is { OwnerGate: true } && !_ctx.State.OwnerApprovedStages.Contains(id))
        {
            _ctx.Events.Emit(new OwnerApprovalRequested { StageId = id });
            _ctx.State.Status = RunStatus.AwaitingOwner;
            _ctx.State.AwaitingOwnerReason = AwaitingOwnerReason.OwnerGate;
            _ctx.Log($"owner-gate: stage {id} green — awaiting owner approval (run `conductor approve` or press R in the TUI)");
            _saveAndReport();
            Notify($"Conductor {_ctx.Plan.Name}: stage {id} is green and awaiting owner approval");
            _ = _telegram.PushWithKeyboardAsync($"Stage {id} green — owner approval needed",
                [("Approve", "approve")], CancellationToken.None);
            return;
        }
        basis ??= GateRunner.ConfirmationBasis(
            stage != null ? GateRunner.ConfiguredForStage(_ctx.Plan, stage) : _ctx.Plan.Gates.Count, _ctx.LastGates);
        if (!_ctx.State.ConfirmedStages.Contains(id)) _ctx.State.ConfirmedStages.Add(id);
        _ctx.State.AwaitingOwnerReason = null;
        _ctx.State.PendingPhaseGate = null;
        _ctx.State.PendingAudit = null;
        _ctx.State.PendingFix = null;
        _ctx.State.AttemptsThisStage = 0;
        if (stage?.PostHook is { } postHook)
            await RunStageHookAsync(id, "post-hook", postHook, ct).ConfigureAwait(false);
        await _lanes.RunFollowupFixLanesAsync(id, ct).ConfigureAwait(false);
        if (_ctx.State.PauseAfterStage)
        {
            _ctx.State.PauseAfterStage = false;
            _ctx.State.Status = RunStatus.Paused;
            _ctx.Log($"✓ phase {id} CONFIRMED ({basis}) — parked (pause-after-stage was set)");
        }
        else
        {
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Log($"✓ phase {id} CONFIRMED ({basis}{(_ctx.State.AuditedStages.Contains(id) ? " + audit" : "")}) — advancing");
        }
        _ctx.Events.Emit(new StageConfirmed { StageId = id, Audited = _ctx.State.AuditedStages.Contains(id) });
        _ctx.Store?.ConfirmStage(_ctx.State.RunId, id);
        // SC6.1: state write FIRST, squash after. The other order collapsed the stage's bookkeeping and
        // then appended to it one second later — devcontext #14 watched stage G2 finish with two
        // identical chore commits despite the cleanup reporting success. Nothing below this line writes
        // history for the stage, so the squash is now the last word on it.
        _saveAndReport();
        SquashBookkeeping(id);
    }

    internal bool HasNextUnconfirmedStage(string stageId)
    {
        var idx = _ctx.Plan.Stages.FindIndex(s => s.Id == stageId);
        if (idx < 0) return false;
        for (var i = idx + 1; i < _ctx.Plan.Stages.Count; i++)
        {
            var sid = _ctx.Plan.Stages[i].Id;
            if (!_ctx.State.SkippedStages.Contains(sid) && !_ctx.State.ConfirmedStages.Contains(sid))
                return true;
        }
        return false;
    }

    internal async Task ApproveAwaitingOwnerAsync(CancellationToken ct)
    {
        var stageId = _ctx.State.CurrentStage
            ?? _ctx.Plan.Stages.FirstOrDefault(s => !_ctx.State.ConfirmedStages.Contains(s.Id) && !_ctx.State.SkippedStages.Contains(s.Id))?.Id;
        switch (OwnerApproval.Decide(_ctx.State.AwaitingOwnerReason))
        {
            case ApprovalOutcome.ResumeSession:
                _ctx.SessionApproved = true;
                _ctx.State.AwaitingOwnerReason = null;
                _ctx.State.Status = RunStatus.Idle;
                if (stageId != null) _ctx.Events.Emit(new OwnerApprovalGranted { StageId = stageId });
                _ctx.Save();
                _ctx.Log("owner approved (approval mode) — running the next session");
                break;
            case ApprovalOutcome.ResetBudgetAndResume:
                // SC2.3: the window is about to be zeroed — record what it held and when the new one
                // opens, BEFORE the zeroing, or the run loses the only account of the spend it is
                // forgiving. The lifetime total (History) is untouched by design: an approval raises
                // the ceiling, it does not un-spend the money.
                var closedWindow = _ctx.RunCostUsd;
                var closedTokens = _ctx.RunTokens;
                _ctx.RunCostUsd = 0;
                _ctx.RunTokens = 0;
                _ctx.RunOverheadUsd = 0;
                _ctx.State.PerRunCostUsd = 0;
                _ctx.State.PerRunTokens = 0;
                _ctx.State.PerRunOverheadCostUsd = 0;
                _ctx.State.BudgetWindowStartedUtc = DateTime.UtcNow;
                _ctx.State.BudgetApprovals++;
                _ctx.State.AwaitingOwnerReason = null;
                _ctx.State.Status = RunStatus.Idle;
                if (stageId != null) _ctx.Events.Emit(new OwnerApprovalGranted { StageId = stageId });
                _ctx.Save();
                _ctx.Log($"owner approved (budget) — window reset to $0.00 after ${closedWindow:0.00} / " +
                         $"{closedTokens / 1000.0:0.#}k; lifetime spend is still ${_ctx.State.TotalCostUsd:0.00} " +
                         $"over {_ctx.State.History.Count} session(s) — approval {_ctx.State.BudgetApprovals}, continuing");
                break;
            default:
                if (stageId == null) { _ctx.State.Status = RunStatus.Idle; _ctx.Save(); break; }
                if (!_ctx.State.OwnerApprovedStages.Contains(stageId))
                {
                    _ctx.Events.Emit(new OwnerApprovalGranted { StageId = stageId });
                    _ctx.State.OwnerApprovedStages.Add(stageId);
                    _ctx.Log($"owner approved stage {stageId} — continuing");
                }
                await ConfirmStageAsync(stageId, ct).ConfigureAwait(false);
                break;
        }
    }

    internal async Task RunStageHookAsync(string stageId, string label, HookConfig hook, CancellationToken ct)
    {
        _ctx.Log($"{label}: {stageId} — {hook.Command}");
        var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? _ctx.Plan.Repo : Path.Combine(_ctx.Plan.Repo, hook.Cwd);
        var r = await ProcessRunner.RunPowerShellAsync(hook.Command, cwd, TimeSpan.FromMinutes(hook.TimeoutMinutes), ct).ConfigureAwait(false);
        var timedOut = r.TimedOut ? " (timed out)" : "";
        _ctx.Log($"{label}: exit {r.ExitCode}{timedOut} in {r.Duration.TotalSeconds:0}s");
        if (r.ExitCode != 0)
        {
            var outputSnippet = r.Output.Length > 500 ? r.Output[..500] + "\n\u2026(truncated)" : r.Output;
            var detail = $"stage {stageId} {label} failed (exit {r.ExitCode}): {hook.Command}";
            _ctx.Log($"ERROR: {detail}\n{outputSnippet.TrimEnd()}");
            if (label == "pre-hook")
                NeedsHuman(detail);
        }
            else if (label == "pre-hook")
                _ctx.State.PreHookRunStages.Add(stageId);
        }

    internal async Task<bool> EscalateExhaustedStageAsync(StageConfig stage, TrackerSnapshot track, int maxAttempts)
    {
        _ctx.Log($"stage {stage.Id} exhausted its attempt budget ({maxAttempts}) — consulting advisor");
        var last = _ctx.State.History.LastOrDefault();
        var verdict = await ConsultAdvisorAsync(last, stage, track, $"attempt budget exhausted ({maxAttempts})").ConfigureAwait(false);
        if (verdict?.Action is AdvisorAction.Skip)
        {
            SkipStage(stage, $"advisor: {verdict.Reason}");
            return false;
        }
        if (verdict?.Action is AdvisorAction.Retry or AdvisorAction.Resume or AdvisorAction.ResetBudget)
        {
            _ctx.Log($"advisor says {verdict.Action} ({verdict.Reason}) — granting {stage.Sessions} more attempts");
            _ctx.State.AttemptsThisStage = maxAttempts - Math.Max(1, stage.Sessions);
            _ctx.Save();
            return true;
        }
        NeedsHuman($"stage {stage.Id} used all {maxAttempts} attempts without completing — inspect and `conductor resume` (or `conductor skip`)" +
                   (verdict != null ? $" · advisor: {verdict.Reason}" : ""));
        return false;
    }
    // ── bookkeeping helpers (private) ──

    private void SquashBookkeeping(string stageId)
    {
        if (_ctx.State.SquashedStages.Contains(stageId)) return;
        if (!_ctx.State.StageStartHeads.TryGetValue(stageId, out var startHead) || string.IsNullOrWhiteSpace(startHead))
        {
            _ctx.Log($"P4 squash: no start-head recorded for stage {stageId} — skipping");
            return;
        }

        // SC6.1: whatever the squash does to the tip, the sha the report publisher was holding is no
        // longer a commit it may amend. Dropped before the attempt, not after, so a crash mid-squash
        // still leaves a state that cannot amend a stranger's commit.
        _ctx.State.LastReportCommitSha = null;
        _ctx.Save();
        _ctx.Log($"P4 squash: collapsing {Git.BookkeepingSubjectPrefix} commits for stage {stageId} since {Short(startHead)}");

        Git.SquashResult result;
        try
        {
            result = Git.SquashChoreCommits(_ctx.Plan.Repo, startHead);
        }
        catch (Exception ex)
        {
            result = new Git.SquashResult(Git.SquashStatus.Failed, $"squash threw: {ex.Message}",
                ExitCode: -1, StdErr: ex.GetType().Name);
        }
        ApplySquashResult(_ctx.State, stageId, result, _ctx.Log);
        _ctx.Save();
    }

    /// <summary>SC6.2: the marking policy, kept apart from the git work so both halves can be measured
    /// against a real result. Only a squash that actually succeeded marks a stage. The old code added
    /// the stage BEFORE the attempt and removed it only on an exception, so a rebase that merely
    /// returned non-zero — four stage closes of six, devcontext #20 — left the stage permanently
    /// marked squashed and unretryable, with git's reason thrown away.</summary>
    /// <returns>true when the stage is now marked squashed.</returns>
    internal static bool ApplySquashResult(RunState state, string stageId, Git.SquashResult result, Action<string, string?> log)
    {
        if (result.AbortedRebase)
            log($"P4 squash: stage {stageId} — aborted a half-finished rebase found in the repo before rewriting anything", "warn");

        if (result.Ok)
        {
            state.SquashedStages.Add(stageId);
            log($"P4 squash: stage {stageId} — {result.Message}", null);
            return true;
        }

        var why = result.Status == Git.SquashStatus.Failed
            ? $"FAILED — {result.Message}: git exit {result.ExitCode}" +
              (string.IsNullOrWhiteSpace(result.StdErr) ? "" : $", stderr: {result.StdErr}") +
              (result.Commands.Count == 0 ? "" : $", last command: {result.Commands[^1]}")
            : $"refused — {result.Message}";
        state.SquashedStages.Remove(stageId);
        log($"P4 squash: stage {stageId} {why} — history unchanged, stage left unsquashed so it can be retried", "warn");
        return false;
    }

    private StageConfig CurrentStageConfig()
        => _ctx.Plan.Stages.FirstOrDefault(s => s.Id == _ctx.State.CurrentStage) ?? _ctx.Plan.Stages[^1];

    private StageConfig StageConfigFor(string stageId)
        => _ctx.Plan.Stages.FirstOrDefault(s => s.Id == stageId) ?? CurrentStageConfig();

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
    private void ParseAuditFollowups(string stageId)
    {
        var handoverPath = Path.Combine(_ctx.StateDir, "handovers", $"{stageId}.md");
        if (!File.Exists(handoverPath)) return;

        var followupsPath = Path.Combine(_ctx.StateDir, "followups.md");
        var existing = File.Exists(followupsPath) ? File.ReadAllText(followupsPath, Encoding.UTF8) : "";

        var bullets = new List<string>();
        try
        {
            var content = File.ReadAllText(handoverPath, Encoding.UTF8);
            var lines = content.Split('\n');
            var inSection = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("## ", StringComparison.Ordinal) || t.StartsWith("### ", StringComparison.Ordinal))
                {
                    var heading = t.ToLowerInvariant();
                    inSection = heading.Contains("weak", StringComparison.Ordinal) || heading.Contains("deferred", StringComparison.Ordinal) ||
                                heading.Contains("bugs not fixed", StringComparison.Ordinal) || heading.Contains("unfixed", StringComparison.Ordinal) ||
                                heading.Contains("concrete follow", StringComparison.Ordinal);
                }
                else if (inSection && (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)
                         || (t.StartsWith("### ", StringComparison.Ordinal) && t.Contains("D-"))))
                {
                    var bullet = t.TrimStart('-', '*', ' ').Trim();
                    if (bullet.Length > 0) bullets.Add(bullet);
                }
            }
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (bullets.Count == 0) return;

        var sb = new StringBuilder();
        var prevExists = existing.Length > 0;
        if (!prevExists)
        {
            sb.AppendLine("# Conductor followups (auto-tracked from audit handovers)");
            sb.AppendLine();
            sb.AppendLine("| Id | Item | Stage | Status |");
            sb.AppendLine("|---|---|---|---|");
        }
        else
        {
            sb.Append(existing.TrimEnd());
        }

        var added = 0;
        var sid = stageId;
        foreach (var bullet in bullets)
        {
            var title = bullet.Length > 80 ? bullet[..77] + "\u2026" : bullet;
            if (existing.Contains(title, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!prevExists && added == 0)
                sb.AppendLine();
            sb.AppendLine($"| FU-{sid}-{added + 1:00} | {title} | {sid} | OPEN |");
            added++;
        }

        if (added > 0)
        {
            File.WriteAllText(followupsPath, sb.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
            _ctx.Log($"followups: {added} new item(s) from {stageId} audit tracked in followups.md");
        }
    }
#pragma warning restore MA0045
}
