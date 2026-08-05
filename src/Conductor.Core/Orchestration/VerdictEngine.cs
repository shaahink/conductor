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
    private readonly RunContext _ctx;
    private readonly GateOrchestrator _gates;
    private readonly LaneCoordinator _lanes;
    private readonly ITelegramService _telegram;
    private readonly WebhookNotifier _webhooks;
    private readonly Action _saveAndReport;
    private readonly Action _pushIdleSnapshot;

    public VerdictEngine(
        RunContext ctx,
        GateOrchestrator gates,
        LaneCoordinator lanes,
        ITelegramService telegram,
        WebhookNotifier webhooks,
        Action saveAndReport,
        Action pushIdleSnapshot)
    {
        _ctx = ctx;
        _gates = gates;
        _lanes = lanes;
        _telegram = telegram;
        _webhooks = webhooks;
        _saveAndReport = saveAndReport;
        _pushIdleSnapshot = pushIdleSnapshot;
    }

    // ── M4.1: claims vs confirmations ──

    private void ConfirmPendingCheckpoints(string stageId, int? sessionNumber = null)
    {
        if (_ctx.State.PendingConfirmation.Count == 0) return;
        var ids = _ctx.State.PendingConfirmation.ToArray();
        // W1.1: this emits CheckpointConfirmed into the work graph — the fold that TrackerGenerator's
        // "DONE ✓" and every other view read. Confirmation stays the engine's verdict alone.
        _ctx.Store?.ConfirmCheckpoints(_ctx.State.RunId, ids, sessionNumber);
        _ctx.Log($"confirmed {ids.Length} checkpoint(s) for stage {stageId}: [{string.Join(", ", ids)}]");
        _ctx.State.PendingConfirmation.Clear();
    }

    // ── static helpers ──

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    // ── instance helpers ──

    private int MaxAttempts(StageConfig stage) => Math.Max(1, stage.Sessions * _ctx.Plan.Limits.StageSlackFactor);

    /// <summary>SC4.3: the ONE place a finished session's commits are collected, primary repo and
    /// declared satellites together. Four call sites used to read <c>Git.CommitsSince</c> on the
    /// primary repo alone, so a session that delivered in a sibling looked idle from every one
    /// of them.</summary>
    private void CollectCommits(SessionRecord rec, string startHead)
    {
        rec.NewCommits = Git.CommitsSince(_ctx.Plan.Repo, startHead);
        rec.SatelliteCommits = SatelliteRepos.CommitsSince(_ctx.Plan, rec.SatelliteStartHeads);
    }

    /// <summary>SC4.1: every battery in this engine goes through here, so this is the one place the
    /// settle has to live. The session judged is the last one on record — the one that just exited
    /// for a session battery, the stage's final one for a phase gate or the closing battery.</summary>
    private Task SettleBeforeGatesAsync(CancellationToken ct) =>
        BatterySettler.SettleAsync(
            _ctx.Store, _ctx.State.RunId,
            _ctx.State.History.Count > 0 ? _ctx.State.History[^1].Number : null,
            _ctx.Plan.Limits.EffectiveBatterySettle, _ctx.LogWithOutcome, ct: ct);

    private async Task<IReadOnlyList<GateResult>> RunGateBatteryAsync(CancellationToken ct, bool fastOnly = false)
    {
        await SettleBeforeGatesAsync(ct).ConfigureAwait(false);
        _ctx.CurGate = fastOnly ? "battery:fast" : "battery:full";
        try
        {
            return await _gates.RunBatteryAsync(_ctx.Log, _ctx.LogWithOutcome, _ctx.Sink.GateProgress, ct, fastOnly).ConfigureAwait(false);
        }
        finally { _ctx.CurGate = null; }
    }

    private void EmitGates(IReadOnlyList<GateResult> gates, string scope, string? sessionId = null)
    {
        _gates.PersistGates(gates, scope, sessionId);
    }

    private void Notify(string message)
    {
        _ = _telegram.PushAsync(message);
        _webhooks.FireAsync(message);

        var n = _ctx.Plan.Notify;
        if (n == null || string.IsNullOrWhiteSpace(n.Command)) return;
        try
        {
            var args = n.Args.Select(a => a.Replace("{message}", message));
#pragma warning disable MA0045 // fire-and-forget notify — sync Run is the caller's expectation, same as original Orchestrator.Plumbing
            ProcessRunner.Run(n.Command, args, _ctx.Plan.Repo, TimeSpan.FromMinutes(1));
#pragma warning restore MA0045
        }
        catch (Exception ex) { _ctx.Log($"notify failed: {ex.Message}"); }
    }

    // ── SessionRunner delegate entry points (public) ──

    public async Task EvaluateSessionAsync(SessionRecord rec, StageConfig stage, TrackerSnapshot preTrack, string startHead,
        bool stalled, bool timedOut, bool killedByUser, bool agentErrored, CancellationToken ct)
    {
        NoteOutsideRepoWrites(rec);
        if (killedByUser)
        {
            rec.Outcome = SessionOutcome.KilledByUser;
            _ctx.State.Status = RunStatus.Paused;
            _ctx.Log("session killed by user — pausing (conductor resume to continue)");
            _saveAndReport();
            return;
        }
        if (stalled || timedOut)
        {
            rec.Outcome = stalled ? SessionOutcome.Stalled : SessionOutcome.TimedOut;
            _ctx.State.AttemptsThisStage++;
            CollectCommits(rec, startHead);
            var prevSession = _ctx.State.History.Count >= 2 ? _ctx.State.History[^2] : null;
            if (_ctx.Plan.Limits.SameFailureCircuitBreaker
                && FailureCircuitBreaker.ShouldBreak(prevSession, rec, null))
            {
                _ctx.Log($"circuit breaker: identical failure pattern detected ({rec.Outcome} ×2) — consulting advisor");
                var breakerVerdict = await ConsultAdvisorAsync(rec, stage, _ctx.Progress.Read(_ctx.Plan, ct),
                    $"identical failure pattern: 2 consecutive {rec.Outcome} sessions with matching symptoms").ConfigureAwait(false);
                await ApplyVerdictAsync(breakerVerdict, rec, stage, defaultAction: AdvisorAction.NeedsHuman).ConfigureAwait(false);
                _saveAndReport();
                return;
            }
            if (stalled && _ctx.Plan.Limits.StallPatternTermination && !_ctx.Plan.Limits.SameFailureCircuitBreaker)
            {
                if (IdenticalStallPattern(rec))
                {
                    NeedsHuman($"identical-stall: {rec.Number - 1} sessions stalled with no commits, no output — environment or agent is broken");
                    return;
                }
                _ctx.StallBackoffMultiplier++;
            }
            else
            {
                _ctx.StallBackoffMultiplier = stalled ? _ctx.StallBackoffMultiplier + 1 : 1;
            }
            if (stalled)
            {
                var delayMinutes = _ctx.Plan.Limits.StallBackoffMinutes * _ctx.StallBackoffMultiplier;
                _ctx.StallBackoffUntil = DateTime.UtcNow.AddMinutes(delayMinutes);
                _ctx.Log($"stall backoff: {delayMinutes}m (multiplier ×{_ctx.StallBackoffMultiplier}) until {_ctx.StallBackoffUntil:HH:mm} UTC");
            }
            else
            {
                _ctx.StallBackoffUntil = null;
            }
            if (rec.ResumeCount < _ctx.Plan.Limits.MaxResumesPerSession)
            {
                QueueResume(rec, stalled ? "session stalled (no output)" : "session hit the hard timeout");
                _ctx.Log($"will resume agent session (resume {rec.ResumeCount + 1}/{_ctx.Plan.Limits.MaxResumesPerSession})");
            }
            else
            {
                var verdict = await ConsultAdvisorAsync(rec, stage, _ctx.Progress.Read(_ctx.Plan, ct), "resume budget exhausted after stall/timeout").ConfigureAwait(false);
                await ApplyVerdictAsync(verdict, rec, stage, defaultAction: AdvisorAction.Retry).ConfigureAwait(false);
            }
            _ctx.State.Status = RunStatus.Idle;
            _saveAndReport();
            return;
        }
        _ctx.StallBackoffMultiplier = 1;

        // SC5.1: "I cannot proceed until T" is a control outcome, not a work verdict — it is judged
        // here, alongside kill/stall/timeout, and for the same reason: there is nothing about the
        // WORK to grade. Paying for a gate battery over a session that spent itself reading a clock
        // is the cost this checkpoint exists to remove.
        if (BlockedUntilDuringSession(rec) is { } blockRequest
            && HonourBlockedUntil(rec, stage, blockRequest, startHead))
            return;

        if (rec.Kind == SessionKind.Audit)
        {
            CollectCommits(rec, startHead);
            RecordNonDeliveryClaims(rec); // SF0.2 (bug #10)
            rec.Outcome = SessionOutcome.Progress;
            if (!_ctx.State.AuditedStages.Contains(stage.Id)) _ctx.State.AuditedStages.Add(stage.Id);
            _ctx.State.PendingAudit = null;
            _ctx.State.PendingPhaseGate = new PendingPhaseGate
            {
                StageId = stage.Id,
                StageStartHead = _ctx.State.CurrentStageStartHead ?? startHead,
            };
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Log($"audit session #{rec.Number} complete ({rec.NewCommits.Count} commits) — re-verifying phase {stage.Id} with full battery");
            ParseAuditFollowups(stage.Id);
            _saveAndReport();
            return;
        }

        if (rec.Kind == SessionKind.Verify)
        {
            CollectCommits(rec, startHead);
            // SF0.2 (bug #10): read the claim BEFORE the pass/fail split, so ConfirmPendingCheckpoints
            // below sees it. A claim landing mid-verify is covered by the verdict this session is
            // about to return on this very tree — if the verifier passes, it is confirmed with the
            // rest; if it fails, ConfirmPendingCheckpoints does not run and the claim correctly stays
            // pending for the fix session and the verification after it.
            RecordNonDeliveryClaims(rec);
            var verdict = Verifier.Parse(rec.ResultSummary ?? "");
            if (verdict != null)
            {
                var findingsText = string.Join("\n", verdict.Findings);
                _ctx.Store?.WriteScore(_ctx.State.RunId, rec.Number, stage.Id, verdict.Score,
                    verdict.Verdict, findingsText);
                _ctx.Log($"verifier score: {verdict.Score}/100 — verdict: {verdict.Verdict} ({verdict.Findings.Count} finding(s))");

                // P2: the QA dial's threshold wins over limits.verifierThreshold when set
                var threshold = _ctx.Qa.EffectiveVerifierThreshold(_ctx.Plan, stage);
                if (verdict.Passes(threshold))
                {
                    rec.Outcome = SessionOutcome.Progress;
                    _ctx.State.AttemptsThisStage = 0;
                    if (verdict.Findings.Count > 0)
                        WriteVerifierFollowups(stage.Id, verdict);
                    _ctx.Log($"verifier passed ({verdict.Score}/{threshold}) — {(verdict.Findings.Count > 0 ? $"{verdict.Findings.Count} finding(s) tracked as follow-ups" : "no findings")}");

                    // M4.1: confirm checkpoints claimed by the preceding deliver session
                    ConfirmPendingCheckpoints(stage.Id, rec.Number);

                    // M3.1: workflow-driven next step
                    AdvanceWorkflowStep(stage, rec, gatesGreen: true, verifierScore: verdict.Score,
                        verifierPassed: true, circuitBroken: false, sessionStartHead: startHead, preTrack: preTrack);
                }
                else
                {
                    _ctx.State.PendingFix = new PendingFix
                    {
                        FromSession = rec.Number,
                        VerifierFindings = findingsText,
                        VerifierScore = verdict.Score,
                        GateFailures = $"verifier score {verdict.Score}/100 < threshold {threshold}",
                        ProgressSummary = $"Verifier verdict: {verdict.Verdict}. " +
                            (verdict.Findings.Count > 0
                                ? $"Findings: {string.Join("; ", verdict.Findings.Take(5))}"
                                : "No specific findings recorded."),
                    };
                    rec.Outcome = SessionOutcome.NoProgress;
                    _ctx.State.AttemptsThisStage++;
                    _ctx.Log($"verifier failed ({verdict.Score}/{threshold}) — queuing fix session with {verdict.Findings.Count} finding(s)");

                    // M3.1: workflow-driven next step after failed verify
                    AdvanceWorkflowStep(stage, rec, gatesGreen: false, verifierScore: verdict.Score,
                        verifierPassed: false, circuitBroken: false, sessionStartHead: startHead, preTrack: preTrack);
                }
            }
            else
            {
                rec.Outcome = SessionOutcome.AgentError;
                _ctx.Log("verifier produced no parseable score — treating as agent error, queuing fix");
                _ctx.State.PendingFix = new PendingFix
                {
                    FromSession = rec.Number,
                    GateFailures = "verifier session produced no valid score JSON",
                    ProgressSummary = $"The verifier agent session ended but its output could not be parsed. Check session-{rec.Number:000}.jsonl for raw output.",
                };
                _ctx.State.AttemptsThisStage++;
            }
            _ctx.State.Status = RunStatus.Idle;
            _saveAndReport();
            return;
        }

        _ctx.State.Status = RunStatus.VerifyingGates;
        _ctx.Save();
        _pushIdleSnapshot();

        // M3.2: skip gates when overridden
        IReadOnlyList<GateResult> gates;
        if (_ctx.State.SkipGatesThisStage)
        {
            _ctx.Log("gate battery SKIPPED (per-stage override: skipGates)");
            gates = Array.Empty<GateResult>();
        }
        else
        {
            _ctx.Log(_ctx.Plan.PerPhaseGates
                ? "verifying independently: fast gates + git + tracker diff (full battery at phase end)"
                : "verifying independently: gate battery + git + tracker diff");
            gates = await RunGateBatteryAsync(ct, fastOnly: _ctx.Plan.PerPhaseGates).ConfigureAwait(false);
        }
        _ctx.LastGates = gates;
        rec.GateSummary = GateRunner.Summary(gates);
        EmitGates(gates, "session", rec.Number.ToString());
        var sessionOverhead = gates.Sum(g => g.EstimatedCostUsd(_ctx.Plan.Limits.OverheadCostPerSecond));
        rec.OverheadCostUsd = sessionOverhead;
        _ctx.RunOverheadUsd += sessionOverhead;
        _ctx.State.PerRunOverheadCostUsd = _ctx.RunOverheadUsd;

        if (ct.IsCancellationRequested)
        {
            rec.Outcome = SessionOutcome.Interrupted;
            QueueResume(rec, "conductor was cancelled during gate verification");
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Log("verification interrupted — will re-verify on resume (no fix queued)");
            _saveAndReport();
            return;
        }

        var postTrack = _ctx.Progress.Read(_ctx.Plan, ct);
        CollectCommits(rec, startHead);

        rec.NewlyDone = ResolveClaims(rec, stage.Id, preTrack, postTrack);

        var newlyBlocked = postTrack.Checkpoints
            .Where(c => c.IsBlocked && !(preTrack.ById(c.Id)?.IsBlocked ?? false))
            .Select(c => c.Id).ToList();
        var gatesGreen = GateRunner.AllRequiredPassed(gates);
        var dirty = Git.IsDirty(_ctx.Plan.Repo);

        // W1.3: stage completeness consults the graph too — a stage whose last item was claimed
        // only via the graph is complete NOW, not one tracker regeneration later.
        var stageComplete = postTrack.StageDone(stage.Id) || GraphStageDone(stage.Id);

        // SC4.2: the commit count the verdict acts on is the AGENT's commits. Conductor's own
        // chore(conductor): status writes land inside the session window too, and counting them
        // scored a session green for the engine's own bookkeeping (devcontext #14).
        // SC4.3: and they are the agent's commits WHEREVER the plan says work may land — a stage
        // delivered in a declared satellite is delivery, not an empty git log.
        var workCommits = SessionProgress.WorkCommits(rec);
        var allRaw = rec.NewCommits.Count + rec.SatelliteCommits.Count;
        var bookkeeping = allRaw - workCommits.Count;
        var bookkeepingNote = bookkeeping > 0 ? $" (+{bookkeeping} conductor bookkeeping, not counted)" : "";
        var satelliteWork = Git.ExcludeBookkeeping(rec.SatelliteCommits);
        var satelliteNote = satelliteWork.Count > 0
            ? $" (incl. {satelliteWork.Count} in satellite repo(s): {string.Join("; ", satelliteWork.Take(4).Select(c => Trunc(c, 60)))})"
            : "";

        _ctx.Log($"verdict inputs: {GateRunner.Token(gates)} · commits {workCommits.Count}{satelliteNote}{bookkeepingNote} · newly DONE [{string.Join(",", rec.NewlyDone)}] · dirty {(dirty ? "YES" : "no")}", gatesGreen ? "pass" : "fail");

        if (newlyBlocked.Count > 0 && _ctx.Plan.PauseOnBlocked)
        {
            NeedsHuman($"checkpoint(s) newly BLOCKED: {string.Join(", ", newlyBlocked)} — see tracker handoff");
            _saveAndReport();
            return;
        }

        // SC4.2/SC4.3: NoProgress has to mean no progress. A checkpoint claimed through the work
        // graph IS delivery even when this repo's git log is empty, and so is a commit in a declared
        // satellite — sk #3 scored NoProgress twice for a stage delivered in a sibling repo, in a
        // plan written to avoid exactly that. workCommits now spans every repo the plan declares.
        if (gatesGreen && (workCommits.Count > 0 || rec.NewlyDone.Count > 0 || stageComplete) && !agentErrored)
        {
            // M3.1: workflow-driven next step instead of hardcoded ShouldVerify
            _ctx.State.AttemptsThisStage = rec.NewlyDone.Count > 0 ? 0 : _ctx.State.AttemptsThisStage;
            _ctx.State.PendingFix = null;
            rec.Outcome = rec.NewlyDone.Count > 0 ? SessionOutcome.Advanced : SessionOutcome.Progress;
            if (dirty) _ctx.Log($"note: working tree left dirty after green session: {Git.DirtySummary(_ctx.Plan.Repo)}");

            // M4.1: queue checkpoints for confirmation after verifier passes (or skip).
            // K1.1: UNION, not replace. A claim can now be queued by a session that never reaches
            // this line — a rollover, an audit, a verify — and confirmation happens later, at the
            // phase gate. Overwriting here dropped those on the floor the moment the next delivery
            // session claimed anything of its own, which is the SF0.2 invisibility bug by another road.
            foreach (var id in rec.NewlyDone)
                if (!_ctx.State.PendingConfirmation.Contains(id, StringComparer.OrdinalIgnoreCase))
                    _ctx.State.PendingConfirmation.Add(id);

            AdvanceWorkflowStep(stage, rec, gatesGreen: true, verifierScore: null,
                verifierPassed: false, circuitBroken: false, stageComplete: stageComplete,
                sessionStartHead: startHead, preTrack: preTrack);

            if (_ctx.Plan.PerPhaseGates && stageComplete)
            {
                ScheduleGateOrAudit(stage.Id, _ctx.State.CurrentStageStartHead ?? startHead);
            }

            _ctx.Log($"session #{rec.Number} {rec.Outcome} — {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) + " done" : "no checkpoint flipped yet")}", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
        }
        else
        {
            rec.Outcome = agentErrored ? SessionOutcome.AgentError : gatesGreen ? SessionOutcome.NoProgress : SessionOutcome.GatesRed;
            _ctx.State.AttemptsThisStage++;
            var prevSession = _ctx.State.History.Count >= 2 ? _ctx.State.History[^2] : null;
            if (_ctx.Plan.Limits.SameFailureCircuitBreaker
                && FailureCircuitBreaker.ShouldBreak(prevSession, rec, gates))
            {
                _ctx.Log($"circuit breaker: identical failure pattern detected ({rec.Outcome} ×2) — consulting advisor");
                var breakerVerdict = await ConsultAdvisorAsync(rec, stage, _ctx.Progress.Read(_ctx.Plan, ct),
                    $"identical failure pattern: 2 consecutive {rec.Outcome} sessions with matching symptoms").ConfigureAwait(false);
                await ApplyVerdictAsync(breakerVerdict, rec, stage, defaultAction: AdvisorAction.NeedsHuman).ConfigureAwait(false);
                _ctx.State.Status = RunStatus.Idle;
                _saveAndReport();
                return;
            }
            _ctx.State.PendingFix = new PendingFix
            {
                FromSession = rec.Number,
                GateFailures = GateRunner.FailureDetails(gates),
                // SC4.2: the fix session is told the same number the verdict acted on. Telling it
                // "new commits: 3" when three were conductor's own status writes sends it hunting
                // for work that was never done.
                ProgressSummary = $"new commits: {workCommits.Count}" +
                                  (workCommits.Count > 0 ? $" ({string.Join("; ", workCommits.Take(5))})" : "") +
                                  (bookkeeping > 0 ? $" · {bookkeeping} conductor bookkeeping commit(s) excluded" : "") +
                                  $" · newly DONE: {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) : "none")}" +
                                  $" · working tree: {(dirty ? "DIRTY — " + Git.DirtySummary(_ctx.Plan.Repo) : "clean")}" +
                                  (agentErrored ? " · agent process reported an error result" : ""),
            };
            // SC2.2: NextAttemptNumber — the number the queued session will announce itself with.
            _ctx.Log($"session #{rec.Number} {rec.Outcome} — queuing fix session (attempt {_ctx.State.NextAttemptNumber}/{MaxAttempts(stage)})", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
        }
        _ctx.State.Status = RunStatus.Idle;
        _saveAndReport();
    }

    public void ReflectionStep(SessionRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.ResultSummary)) return;

        var text = rec.ResultSummary;
        var idx = text.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var difficulty = text[(idx + "SESSION-RESULT:".Length)..].Trim();
        if (difficulty.Length == 0) return;

        // K1.3: the whole SESSION-RESULT goes in, un-truncated. It used to be cut at 500 characters
        // and pasted verbatim, which is what made lessons.md a file of narratives sheared mid-word.
        // LessonsManager extracts the rule-shaped sentences and writes nothing when there are none \u2014
        // so a status-only result now costs the next prompt nothing instead of 500 characters of it.
        _ctx.Lessons.Append(rec.Stage, rec.Number, difficulty);
    }

    // ── main-loop entry points: closing the plan lives in VerdictEngine.Completion.cs ──

    /// <summary>W3.1: the operator notification path (Telegram + webhook + notify command), exposed
    /// so the session watchdog can raise a hung or stalled session the moment it kills it. Before
    /// W3.1 only a NeedsHuman park ever notified, so a hang stayed silent until a human looked.</summary>
    public void NotifyOperator(string message) => Notify(message);

    public void NeedsHuman(string reason)
    {
        _ctx.State.Status = RunStatus.NeedsHuman;
        _ctx.State.SetAttention(reason);
        _ctx.Events.Emit(new AttentionRequested { Reason = reason });
        _ctx.Log($"🛑 NEEDS HUMAN: {reason}");
        _saveAndReport();
        Notify($"Conductor {_ctx.Plan.Name}: needs attention — {reason}");
        _ = _telegram.PushWithKeyboardAsync(reason,
        [
            ("Resume", "resume"),
            ("Skip Stage", "skip"),
            ("Inject\u2026", "inject:needsHuman"),
            ("Chat", "chat:needsHuman"),
        ]);
    }

}
