using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS6.4 — the session-verdict pipeline: gather, decide, apply. The deciding is not here. It is
/// <see cref="SessionVerdict.Decide"/>, a pure function over <see cref="SessionEvidence"/> that has
/// never heard of a run; everything in this file is the impure half — the readings taken off a
/// finished session, and the state the engine moves once the taxonomy has settled what happened.
/// </summary>
public sealed partial class VerdictEngine
{
    /// <summary>SC7.1 (devcontext #11): the verdict says what the session did OUTSIDE the work tree.
    /// Collected by <c>SessionRunner.TrackActivity</c> from the structured tool events — which is the
    /// whole reason SC7.1 had to stop cutting tool arguments mid-string: a <c>file_path</c> past the
    /// old 150-character cut was never captured, so no verdict could ever have mentioned it.</summary>
    /// <remarks>Raised FIRST in <see cref="EvaluateSessionAsync"/>, ahead of every early return, so a
    /// session that stalls or is killed still reports where it wrote. It is a note, not a judgement:
    /// writing outside the repo is often correct (a scratch rig, a satellite the plan forgot to
    /// declare) and the operator is the one who can tell.</remarks>
    private void NoteOutsideRepoWrites(SessionRecord rec)
    {
        if (rec.OutsideRepoWrites.Count == 0) return;
        var shown = rec.OutsideRepoWrites.Take(4).Select(p => Trunc(p, 120));
        var more = rec.OutsideRepoWrites.Count > 4 ? $", +{rec.OutsideRepoWrites.Count - 4} more" : "";
        _ctx.Log($"note: {rec.OutsideRepoWrites.Count} file(s) written outside the repo: {string.Join(", ", shown)}{more}");
    }

    /// <summary>Two consecutive stalls that produced nothing at all — no work commits, no result
    /// text. The environment or the agent is broken, and a third attempt buys nothing.</summary>
    private bool IdenticalStallPattern(SessionRecord rec)
    {
        // SC4.2: a stall that produced only conductor's own bookkeeping commits produced nothing.
        // SC4.3: a stall that committed to a declared satellite produced something.
        if (SessionProgress.HasWorkCommits(rec)) return false;
        var summary = rec.ResultSummary?.Trim();
        if (!string.IsNullOrEmpty(summary)) return false;

        var stalledCount = 1;
        for (var i = _ctx.State.History.Count - 2; i >= 0; i--)
        {
            var prev = _ctx.State.History[i];
            if (prev.Outcome != SessionOutcome.Stalled) break;
            if (!SessionProgress.HasWorkCommits(prev) && string.IsNullOrEmpty(prev.ResultSummary?.Trim()))
            {
                stalledCount++;
                if (stalledCount >= 2) return true;
            }
            else break;
        }
        return false;
    }

    // ── KS6.4: gather → decide → apply ──
    //
    // What used to be one 300-line method with the judgement wired through the I/O is now three
    // gathers, three calls to a pure function that has never heard of a run, and an apply. The
    // ordering the engine relies on is unchanged and deliberately so: every early return before
    // RunGateBattery is a gate battery this run does not pay for, and that used to be a property of
    // statement order rather than of anything a test could name.

    /// <summary>The rows that cost nothing to gather. Everything they settle is a battery unbought,
    /// which is why they are asked first.</summary>
    private SessionEvidence ControlEvidence(SessionRecord rec, StageConfig stage,
        bool stalled, bool timedOut, bool killedByUser, bool agentErrored, VerifierVerdict? verifier)
    {
        var breakerOn = _ctx.Plan.Limits.SameFailureCircuitBreaker;
        var patternOn = _ctx.Plan.Limits.StallPatternTermination;
        return new SessionEvidence
        {
            Kind = rec.Kind,
            SessionNumber = rec.Number,
            KilledByUser = killedByUser,
            Stalled = stalled,
            TimedOut = timedOut,
            AgentErrored = agentErrored,
            ResumeCount = rec.ResumeCount,
            MaxResumesPerSession = _ctx.Plan.Limits.MaxResumesPerSession,
            PriorStallBackoffMultiplier = _ctx.StallBackoffMultiplier,
            StallBackoffMinutes = _ctx.Plan.Limits.StallBackoffMinutes,
            StallPatternTerminationEnabled = patternOn,
            // Both detectors are gathered under exactly the guards that used to short-circuit them,
            // so this refactor asks the store and the history no more often than the method did.
            IdenticalStallPattern = stalled && patternOn && !breakerOn && IdenticalStallPattern(rec),
            CircuitBreakerEnabled = breakerOn,
            SameFailurePattern = breakerOn && (stalled || timedOut)
                && FailureCircuitBreaker.ShouldBreak(PreviousSession(), rec, null),
            PauseOnBlocked = _ctx.Plan.PauseOnBlocked,
            VerifierParsed = verifier != null,
            VerifierScore = verifier?.Score ?? 0,
            VerifierThreshold = _ctx.Qa.EffectiveVerifierThreshold(_ctx.Plan, stage),
        };
    }

    private SessionRecord? PreviousSession() =>
        _ctx.State.History.Count >= 2 ? _ctx.State.History[^2] : null;

    /// <summary>The impure half of the delivery pass: the lists and strings the decision does not need
    /// but the verdict-inputs line, the fix brief and the workflow step do.</summary>
    private sealed record WorkPass(
        IReadOnlyList<GateResult> Gates,
        IReadOnlyList<string> WorkCommits,
        int Bookkeeping);

    // ── SessionRunner delegate entry points (public) ──

    public async Task EvaluateSessionAsync(SessionRecord rec, StageConfig stage, TrackerSnapshot preTrack, string startHead,
        bool stalled, bool timedOut, bool killedByUser, bool agentErrored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rec);
        NoteOutsideRepoWrites(rec);

        // Parsing the verifier's result is string work over a field already in memory, so it is a free
        // row like the rest; nothing is written anywhere until the decision says to.
        var verifier = rec.Kind == SessionKind.Verify ? Verifier.Parse(rec.ResultSummary ?? "") : null;
        var evidence = ControlEvidence(rec, stage, stalled, timedOut, killedByUser, agentErrored, verifier);

        // SC5.1: the wait row costs a store read, so it is bought on the same terms as the battery —
        // only once the free rows have failed to settle the session.
        BlockedUntilRequested? blockRequest = null;
        if (!killedByUser && !stalled && !timedOut)
        {
            blockRequest = BlockedUntilDuringSession(rec);
            evidence = evidence with { BlockedUntilRequested = blockRequest != null };
        }

        var decision = SessionVerdict.Decide(evidence);
        ApplyBackoff(decision.Backoff);

        if (decision.Disposition == VerdictDisposition.HonourBlockUntil)
        {
            // A window that has already opened is not a reason to skip judging the session: the row
            // is dropped and the taxonomy asked again, which is the same fall-through as before.
            if (HonourBlockedUntil(rec, stage, blockRequest!, startHead)) return;
            evidence = evidence with { BlockedUntilRequested = false };
            decision = SessionVerdict.Decide(evidence);
        }

        if (decision.Disposition != VerdictDisposition.RunGateBattery)
        {
            await ApplyControlAsync(decision, rec, stage, preTrack, startHead, verifier, ct).ConfigureAwait(false);
            return;
        }

        var gates = await GateEvidenceAsync(rec, ct).ConfigureAwait(false);
        evidence = evidence with { GatesRun = true, Cancelled = ct.IsCancellationRequested };
        decision = SessionVerdict.Decide(evidence);
        if (decision.Disposition == VerdictDisposition.Interrupted)
        {
            Stamp(decision, rec);
            QueueResume(rec, decision.Reason);
            if (decision.Log is { } interrupted) _ctx.Log(interrupted);
            Settle(decision);
            return;
        }

        var (workEvidence, pass) = WorkEvidence(evidence, gates, rec, stage, preTrack, startHead, ct);
        decision = SessionVerdict.Decide(workEvidence);

        // KS4.5: the judge is consulted HERE, after the decision exists and before anything applies it,
        // and it is handed that decision as settled fact. The rows it returns are recorded and reported;
        // the decision is never recomputed, which is what makes "evidence, never verdict" a property of
        // the control flow rather than a promise in a comment.
        workEvidence = await JudgeSessionAsync(workEvidence, decision, rec, stage, pass, ct).ConfigureAwait(false);

        await ApplyDeliveryAsync(decision, workEvidence, pass, rec, stage, preTrack, startHead, ct).ConfigureAwait(false);
    }

    // ── the two gathers the run pays for ──

    /// <summary>The battery, its cost accounting and its persistence: the first evidence in this path
    /// that costs real money, which is the whole reason the cheap rows are asked first.</summary>
    private async Task<IReadOnlyList<GateResult>> GateEvidenceAsync(SessionRecord rec, CancellationToken ct)
    {
        _ctx.State.Status = RunStatus.VerifyingGates;
        _ctx.Save();
        _pushIdleSnapshot();

        IReadOnlyList<GateResult> gates;
        if (_ctx.State.SkipGatesThisStage)   // M3.2: skip gates when overridden
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
        return gates;
    }

    /// <summary>The tracker diff and the commit collection — the second thing the run pays for, and
    /// the last rows the taxonomy is missing.</summary>
    private (SessionEvidence Evidence, WorkPass Pass) WorkEvidence(
        SessionEvidence e, IReadOnlyList<GateResult> gates, SessionRecord rec, StageConfig stage,
        TrackerSnapshot preTrack, string startHead, CancellationToken ct)
    {
        var postTrack = _ctx.Progress.Read(_ctx.Plan, ct);
        CollectCommits(rec, startHead);
        rec.NewlyDone = ResolveClaims(rec, stage.Id, preTrack, postTrack);

        var newlyBlocked = postTrack.Checkpoints
            .Where(c => c.IsBlocked && !(preTrack.ById(c.Id)?.IsBlocked ?? false))
            .Select(c => c.Id).ToList();

        // SC4.2: the commit count the verdict acts on is the AGENT's commits. Conductor's own
        // chore(conductor): status writes land inside the session window too, and counting them
        // scored a session green for the engine's own bookkeeping (devcontext #14).
        // SC4.3: and they are the agent's commits WHEREVER the plan says work may land — a stage
        // delivered in a declared satellite is delivery, not an empty git log.
        var workCommits = SessionProgress.WorkCommits(rec);
        var bookkeeping = rec.NewCommits.Count + rec.SatelliteCommits.Count - workCommits.Count;

        var evidence = e with
        {
            WorkEvidenceRead = true,
            GatesGreen = GateRunner.AllRequiredPassed(gates),
            // KS4.2: from the same battery, in the same pass — the class's finding, carried as its
            // own row so the verdict can name it rather than reporting "a gate failed".
            Regressions = gates.Where(g => g.HasRegressions)
                .Select(g => new RegressionEvidence(g.Name, g.Regressions, g.RegressionNote)).ToList(),
            // KS4.3: the same, for the mutation class. A separate row rather than a second kind of
            // regression, because the fix they ask for is the opposite one - a regression says put
            // back the check you removed, a shortfall says the checks you kept assert nothing.
            MutationShortfalls = gates.Where(g => g.HasMutationShortfall && g.Mutation is not null)
                .Select(g => new MutationEvidence(g.Name, g.Mutation!.Score, g.Mutation.Threshold,
                    g.Mutation.Counted, g.Mutation.Survivors, g.Mutation.Note)).ToList(),
            WorkCommitCount = workCommits.Count,
            NewlyDoneCount = rec.NewlyDone.Count,
            NewlyBlocked = newlyBlocked,
            // W1.3: stage completeness consults the graph too — a stage whose last item was claimed
            // only via the graph is complete NOW, not one tracker regeneration later.
            StageComplete = postTrack.StageDone(stage.Id) || GraphStageDone(stage.Id),
            WorkingTreeDirty = Git.IsDirty(_ctx.Plan.Repo),
            SameFailurePattern = _ctx.Plan.Limits.SameFailureCircuitBreaker
                && FailureCircuitBreaker.ShouldBreak(PreviousSession(), rec, gates),
        };
        return (evidence, new WorkPass(gates, workCommits, bookkeeping));
    }

    // ── applying a decision ──

    /// <summary>Everything a decision does to the counters, in one place. The attempt increment used
    /// to be written out at five call sites and skipped at two, and nothing could see the difference.</summary>
    private void Stamp(VerdictDecision d, SessionRecord rec)
    {
        if (d.Outcome is { } outcome) rec.Outcome = outcome;
        _ctx.State.AttemptsThisStage = d.Attempts switch
        {
            AttemptEffect.Increment => _ctx.State.AttemptsThisStage + 1,
            AttemptEffect.Reset => 0,
            _ => _ctx.State.AttemptsThisStage,
        };
    }

    private void ApplyBackoff(StallBackoffPlan? plan)
    {
        if (plan is null) return;
        _ctx.StallBackoffMultiplier = plan.Multiplier;
        if (!plan.TouchesUntil) return;
        if (plan.DelayMinutes is { } minutes)
        {
            _ctx.StallBackoffUntil = DateTime.UtcNow.AddMinutes(minutes);
            _ctx.Log($"stall backoff: {minutes}m (multiplier ×{plan.Multiplier}) until {_ctx.StallBackoffUntil:HH:mm} UTC");
        }
        else
        {
            _ctx.StallBackoffUntil = null;
        }
    }

    /// <summary>The tail every applied decision shares. A park is the exception and does not come
    /// through here: <see cref="NeedsHuman"/> sets its own status and saves.</summary>
    private void Settle(VerdictDecision d)
    {
        if (d.ReturnToIdle) _ctx.State.Status = RunStatus.Idle;
        _saveAndReport();
    }

    private async Task AdviseAsync(VerdictDecision d, SessionRecord rec, StageConfig stage, CancellationToken ct)
    {
        if (d.Log is { } line) _ctx.Log(line);
        var advice = await ConsultAdvisorAsync(rec, stage, _ctx.Progress.Read(_ctx.Plan, ct), d.Reason).ConfigureAwait(false);
        await ApplyVerdictAsync(advice, rec, stage, d.AdvisorDefault).ConfigureAwait(false);
    }

    /// <summary>Decisions reached before the battery: nothing here has graded any work.</summary>
    private async Task ApplyControlAsync(VerdictDecision d, SessionRecord rec, StageConfig stage,
        TrackerSnapshot preTrack, string startHead, VerifierVerdict? verifier, CancellationToken ct)
    {
        switch (d.Disposition)
        {
            case VerdictDisposition.PauseKilled:
                Stamp(d, rec);
                _ctx.State.Status = RunStatus.Paused;
                if (d.Log is { } killed) _ctx.Log(killed);
                _saveAndReport();
                return;

            // The stall family. Every one of them collects the session's commits first, because the
            // advisor and the park message both read them; the kill path above never did.
            case VerdictDisposition.Resume:
            case VerdictDisposition.ConsultAdvisor:
            case VerdictDisposition.ParkForHuman:
                Stamp(d, rec);
                CollectCommits(rec, startHead);
                if (d.Disposition == VerdictDisposition.ParkForHuman) { NeedsHuman(d.Reason); return; }
                if (d.Disposition == VerdictDisposition.Resume)
                {
                    QueueResume(rec, d.Reason);
                    if (d.Log is { } resuming) _ctx.Log(resuming);
                }
                else
                {
                    await AdviseAsync(d, rec, stage, ct).ConfigureAwait(false);
                }
                Settle(d);
                return;

            case VerdictDisposition.AuditComplete:
                CollectCommits(rec, startHead);
                RecordNonDeliveryClaims(rec);   // SF0.2 (bug #10)
                Stamp(d, rec);
                if (!_ctx.State.AuditedStages.Contains(stage.Id)) _ctx.State.AuditedStages.Add(stage.Id);
                _ctx.State.PendingAudit = null;
                _ctx.State.PendingPhaseGate = new PendingPhaseGate
                {
                    StageId = stage.Id,
                    StageStartHead = _ctx.State.CurrentStageStartHead ?? startHead,
                };
                _ctx.Log($"audit session #{rec.Number} complete ({rec.NewCommits.Count} commits) — re-verifying phase {stage.Id} with full battery");
                ParseAuditFollowups(stage.Id);
                Settle(d);
                return;

            default:
                ApplyVerify(d, rec, stage, preTrack, startHead, verifier);
                Settle(d);
                return;
        }
    }

    private void ApplyVerify(VerdictDecision d, SessionRecord rec, StageConfig stage,
        TrackerSnapshot preTrack, string startHead, VerifierVerdict? verifier)
    {
        CollectCommits(rec, startHead);
        // SF0.2 (bug #10): read the claim BEFORE the pass/fail split, so ConfirmPendingCheckpoints
        // below sees it. A claim landing mid-verify is covered by the verdict this session is about
        // to return on this very tree — if the verifier passes, it is confirmed with the rest; if it
        // fails, ConfirmPendingCheckpoints does not run and the claim correctly stays pending for the
        // fix session and the verification after it.
        RecordNonDeliveryClaims(rec);
        Stamp(d, rec);

        if (d.Disposition == VerdictDisposition.VerifyUnparseable)
        {
            if (d.Log is { } unparseable) _ctx.Log(unparseable);
            _ctx.State.PendingFix = new PendingFix
            {
                FromSession = rec.Number,
                GateFailures = "verifier session produced no valid score JSON",
                ProgressSummary = $"The verifier agent session ended but its output could not be parsed. Check session-{rec.Number:000}.jsonl for raw output.",
            };
            return;
        }

        var v = verifier!;
        var findingsText = string.Join("\n", v.Findings);
        _ctx.Store?.WriteScore(_ctx.State.RunId, rec.Number, stage.Id, v.Score, v.Verdict, findingsText);
        _ctx.Log($"verifier score: {v.Score}/100 — verdict: {v.Verdict} ({v.Findings.Count} finding(s))");

        var threshold = _ctx.Qa.EffectiveVerifierThreshold(_ctx.Plan, stage);   // P2: the QA dial wins
        if (d.Disposition == VerdictDisposition.VerifyPassed)
        {
            if (v.Findings.Count > 0) WriteVerifierFollowups(stage.Id, v);
            _ctx.Log($"verifier passed ({v.Score}/{threshold}) — {(v.Findings.Count > 0 ? $"{v.Findings.Count} finding(s) tracked as follow-ups" : "no findings")}");

            // M4.1: confirm checkpoints claimed by the preceding deliver session
            ConfirmPendingCheckpoints(stage.Id, rec.Number);

            // M3.1: workflow-driven next step
            AdvanceWorkflowStep(stage, rec, gatesGreen: true, verifierScore: v.Score,
                verifierPassed: true, circuitBroken: false, sessionStartHead: startHead, preTrack: preTrack);
            return;
        }

        _ctx.State.PendingFix = new PendingFix
        {
            FromSession = rec.Number,
            VerifierFindings = findingsText,
            VerifierScore = v.Score,
            GateFailures = d.Reason,
            ProgressSummary = $"Verifier verdict: {v.Verdict}. " +
                (v.Findings.Count > 0
                    ? $"Findings: {string.Join("; ", v.Findings.Take(5))}"
                    : "No specific findings recorded."),
        };
        _ctx.Log($"verifier failed ({v.Score}/{threshold}) — queuing fix session with {v.Findings.Count} finding(s)");

        // M3.1: workflow-driven next step after failed verify
        AdvanceWorkflowStep(stage, rec, gatesGreen: false, verifierScore: v.Score,
            verifierPassed: false, circuitBroken: false, sessionStartHead: startHead, preTrack: preTrack);
    }

    /// <summary>Decisions reached with the whole taxonomy in hand.</summary>
    private async Task ApplyDeliveryAsync(VerdictDecision d, SessionEvidence e, WorkPass w,
        SessionRecord rec, StageConfig stage, TrackerSnapshot preTrack, string startHead, CancellationToken ct)
    {
        var satelliteWork = Git.ExcludeBookkeeping(rec.SatelliteCommits);
        var satelliteNote = satelliteWork.Count > 0
            ? $" (incl. {satelliteWork.Count} in satellite repo(s): {string.Join("; ", satelliteWork.Take(4).Select(c => Trunc(c, 60)))})"
            : "";
        var bookkeepingNote = w.Bookkeeping > 0 ? $" (+{w.Bookkeeping} conductor bookkeeping, not counted)" : "";
        _ctx.Log($"verdict inputs: {GateRunner.Token(w.Gates)} · commits {w.WorkCommits.Count}{satelliteNote}{bookkeepingNote} · newly DONE [{string.Join(",", rec.NewlyDone)}] · dirty {(e.WorkingTreeDirty ? "YES" : "no")}", e.GatesGreen ? "pass" : "fail");

        // KS4.5: the advisory rows are REPORTED beside the inputs and are not among them — printed
        // after the line that says what decided, on its own line, so a reader can see at a glance that
        // the judge spoke and that the verdict did not depend on it. Silent when no judge ran, which
        // keeps every existing run's log byte-identical.
        if (AdvisoryNote(e) is { } advisory) _ctx.Log(advisory);

        if (d.Disposition == VerdictDisposition.ParkForHuman)
        {
            NeedsHuman(d.Reason);
            return;
        }

        Stamp(d, rec);

        if (d.Disposition == VerdictDisposition.Deliver)
        {
            _ctx.State.PendingFix = null;
            if (e.WorkingTreeDirty) _ctx.Log($"note: working tree left dirty after green session: {Git.DirtySummary(_ctx.Plan.Repo)}");

            // M4.1: queue checkpoints for confirmation after verifier passes (or skip).
            // K1.1: UNION, not replace. A claim can now be queued by a session that never reaches
            // this line — a rollover, an audit, a verify — and confirmation happens later, at the
            // phase gate. Overwriting here dropped those on the floor the moment the next delivery
            // session claimed anything of its own, which is the SF0.2 invisibility bug by another road.
            foreach (var id in rec.NewlyDone)
                if (!_ctx.State.PendingConfirmation.Contains(id, StringComparer.OrdinalIgnoreCase))
                    _ctx.State.PendingConfirmation.Add(id);

            AdvanceWorkflowStep(stage, rec, gatesGreen: true, verifierScore: null,
                verifierPassed: false, circuitBroken: false, stageComplete: e.StageComplete,
                sessionStartHead: startHead, preTrack: preTrack);

            if (_ctx.Plan.PerPhaseGates && e.StageComplete)
                ScheduleGateOrAudit(stage.Id, _ctx.State.CurrentStageStartHead ?? startHead);

            _ctx.Log($"session #{rec.Number} {rec.Outcome} — {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) + " done" : "no checkpoint flipped yet")}", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
        }
        else if (d.Disposition == VerdictDisposition.ConsultAdvisor)
        {
            await AdviseAsync(d, rec, stage, ct).ConfigureAwait(false);
        }
        else
        {
            _ctx.State.PendingFix = new PendingFix
            {
                FromSession = rec.Number,
                GateFailures = GateFailureSpill.Render(w.Gates, _ctx.Plan.StateDir, rec.Number),
                // SC4.2: the fix session is told the same number the verdict acted on. Telling it
                // "new commits: 3" when three were conductor's own status writes sends it hunting
                // for work that was never done.
                ProgressSummary = $"new commits: {w.WorkCommits.Count}" +
                                  (w.WorkCommits.Count > 0 ? $" ({string.Join("; ", w.WorkCommits.Take(5))})" : "") +
                                  (w.Bookkeeping > 0 ? $" · {w.Bookkeeping} conductor bookkeeping commit(s) excluded" : "") +
                                  $" · newly DONE: {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) : "none")}" +
                                  $" · working tree: {(e.WorkingTreeDirty ? "DIRTY — " + Git.DirtySummary(_ctx.Plan.Repo) : "clean")}" +
                                  (e.AgentErrored ? " · agent process reported an error result" : ""),
            };
            // SC2.2: NextAttemptNumber — the number the queued session will announce itself with.
            _ctx.Log($"session #{rec.Number} {rec.Outcome} — queuing fix session (attempt {_ctx.State.NextAttemptNumber}/{MaxAttempts(stage)})", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
        }

        Settle(d);
    }
}
