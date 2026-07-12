using System.Text;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core;

public sealed partial class Orchestrator
{
    private async Task EvaluateSessionAsync(SessionRecord rec, StageConfig stage, TrackerSnapshot preTrack, string startHead,
        bool stalled, bool timedOut, bool killedByUser, bool agentErrored, CancellationToken ct)
    {
        if (killedByUser)
        {
            rec.Outcome = SessionOutcome.KilledByUser;
            state.Status = RunStatus.Paused;
            Log("session killed by user — pausing (conductor resume to continue)");
            SaveAndReport();
            return;
        }
        if (stalled || timedOut)
        {
            rec.Outcome = stalled ? SessionOutcome.Stalled : SessionOutcome.TimedOut;
            state.AttemptsThisStage++;
            rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
            var prevSession = state.History.Count >= 2 ? state.History[^2] : null;
            if (plan.Limits.SameFailureCircuitBreaker
                && FailureCircuitBreaker.ShouldBreak(prevSession, rec, null))
            {
                Log($"circuit breaker: identical failure pattern detected ({rec.Outcome} ×2) — consulting advisor");
                var breakerVerdict = await ConsultAdvisorAsync(rec, stage, _progress.Read(plan, ct),
                    $"identical failure pattern: 2 consecutive {rec.Outcome} sessions with matching symptoms").ConfigureAwait(false);
                await ApplyVerdictAsync(breakerVerdict, rec, stage, defaultAction: AdvisorAction.NeedsHuman).ConfigureAwait(false);
                SaveAndReport();
                return;
            }
            if (stalled && plan.Limits.StallPatternTermination && !plan.Limits.SameFailureCircuitBreaker)
            {
                if (IdenticalStallPattern(rec))
                {
                    NeedsHuman($"identical-stall: {rec.Number - 1} sessions stalled with no commits, no output — environment or agent is broken");
                    return;
                }
                _stallBackoffMultiplier++;
            }
            else
            {
                _stallBackoffMultiplier = stalled ? _stallBackoffMultiplier + 1 : 1;
            }
            if (stalled)
            {
                var delayMinutes = plan.Limits.StallBackoffMinutes * _stallBackoffMultiplier;
                _stallBackoffUntil = DateTime.UtcNow.AddMinutes(delayMinutes);
                Log($"stall backoff: {delayMinutes}m (multiplier ×{_stallBackoffMultiplier}) until {_stallBackoffUntil:HH:mm} UTC");
            }
            else
            {
                _stallBackoffUntil = null;
            }
            if (rec.ResumeCount < plan.Limits.MaxResumesPerSession)
            {
                QueueResume(rec, stalled ? "session stalled (no output)" : "session hit the hard timeout");
                Log($"will resume agent session (resume {rec.ResumeCount + 1}/{plan.Limits.MaxResumesPerSession})");
            }
            else
            {
                var verdict = await ConsultAdvisorAsync(rec, stage, _progress.Read(plan, ct), "resume budget exhausted after stall/timeout").ConfigureAwait(false);
                await ApplyVerdictAsync(verdict, rec, stage, defaultAction: AdvisorAction.Retry).ConfigureAwait(false);
            }
            state.Status = RunStatus.Idle;
            SaveAndReport();
            return;
        }
        _stallBackoffMultiplier = 1;

        if (rec.Kind == SessionKind.Audit)
        {
            rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
            rec.Outcome = SessionOutcome.Progress;
            if (!state.AuditedStages.Contains(stage.Id)) state.AuditedStages.Add(stage.Id);
            state.PendingAudit = null;
            state.PendingPhaseGate = new PendingPhaseGate
            {
                StageId = stage.Id,
                StageStartHead = state.CurrentStageStartHead ?? startHead,
            };
            state.Status = RunStatus.Idle;
            Log($"audit session #{rec.Number} complete ({rec.NewCommits.Count} commits) — re-verifying phase {stage.Id} with full battery");
            ParseAuditFollowups(stage.Id);
            SaveAndReport();
            return;
        }

        if (rec.Kind == SessionKind.Verify)
        {
            rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
            var verdict = Verifier.Parse(ExtractSessionResult(rec.ResultSummary) ?? "");
            if (verdict != null)
            {
                var findingsText = string.Join("\n", verdict.Findings);
                _runDb?.WriteScore(state.RunId, rec.Number, stage.Id, verdict.Score,
                    verdict.Verdict, findingsText);
                Log($"verifier score: {verdict.Score}/100 — verdict: {verdict.Verdict} ({verdict.Findings.Count} finding(s))");

                if (verdict.Passes(plan.Limits.VerifierThreshold))
                {
                    rec.Outcome = SessionOutcome.Progress;
                    state.AttemptsThisStage = 0;
                    if (verdict.Findings.Count > 0)
                        WriteVerifierFollowups(stage.Id, verdict);
                    Log($"verifier passed ({verdict.Score}/{plan.Limits.VerifierThreshold}) — {(verdict.Findings.Count > 0 ? $"{verdict.Findings.Count} finding(s) tracked as follow-ups" : "no findings")}");
                }
                else
                {
                    state.PendingFix = new PendingFix
                    {
                        FromSession = rec.Number,
                        VerifierFindings = findingsText,
                        VerifierScore = verdict.Score,
                        GateFailures = $"verifier score {verdict.Score}/100 < threshold {plan.Limits.VerifierThreshold}",
                        ProgressSummary = $"Verifier verdict: {verdict.Verdict}. " +
                            (verdict.Findings.Count > 0
                                ? $"Findings: {string.Join("; ", verdict.Findings.Take(5))}"
                                : "No specific findings recorded."),
                    };
                    rec.Outcome = SessionOutcome.NoProgress;
                    state.AttemptsThisStage++;
                    Log($"verifier failed ({verdict.Score}/{plan.Limits.VerifierThreshold}) — queuing fix session with {verdict.Findings.Count} finding(s)");
                }
            }
            else
            {
                rec.Outcome = SessionOutcome.AgentError;
                Log("verifier produced no parseable score — treating as agent error, queuing fix");
                state.PendingFix = new PendingFix
                {
                    FromSession = rec.Number,
                    GateFailures = "verifier session produced no valid score JSON",
                    ProgressSummary = $"The verifier agent session ended but its output could not be parsed. Check session-{rec.Number:000}.jsonl for raw output.",
                };
                state.AttemptsThisStage++;
            }
            state.Status = RunStatus.Idle;
            SaveAndReport();
            return;
        }

        state.Status = RunStatus.VerifyingGates;
        Save();
        PushIdleSnapshot();
        Log(plan.PerPhaseGates
            ? "verifying independently: fast gates + git + tracker diff (full battery at phase end)"
            : "verifying independently: gate battery + git + tracker diff");
        var gates = await RunGateBatteryAsync(ct, fastOnly: plan.PerPhaseGates).ConfigureAwait(false);
        _lastGates = gates;
        rec.GateSummary = GateRunner.Summary(gates);
        EmitGates(gates, "session", rec.Number.ToString());
        var sessionOverhead = gates.Sum(g => g.EstimatedCostUsd(plan.Limits.OverheadCostPerSecond));
        rec.OverheadCostUsd = sessionOverhead;
        _runOverheadUsd += sessionOverhead;
        state.PerRunOverheadCostUsd = _runOverheadUsd;

        if (ct.IsCancellationRequested)
        {
            rec.Outcome = SessionOutcome.Interrupted;
            QueueResume(rec, "conductor was cancelled during gate verification");
            state.Status = RunStatus.Idle;
            Log("verification interrupted — will re-verify on resume (no fix queued)");
            SaveAndReport();
            return;
        }

        var postTrack = _progress.Read(plan, ct);
        rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
        rec.NewlyDone = postTrack.Checkpoints
            .Where(c => c.IsDone && !(preTrack.ById(c.Id)?.IsDone ?? false))
            .Select(c => c.Id).ToList();
        var newlyBlocked = postTrack.Checkpoints
            .Where(c => c.IsBlocked && !(preTrack.ById(c.Id)?.IsBlocked ?? false))
            .Select(c => c.Id).ToList();
        var gatesGreen = GateRunner.AllRequiredPassed(gates);
        var dirty = Git.IsDirty(plan.Repo);

        Log($"verdict inputs: gates {(gatesGreen ? "green" : "RED")} · commits {rec.NewCommits.Count} · newly DONE [{string.Join(",", rec.NewlyDone)}] · dirty {(dirty ? "YES" : "no")}", gatesGreen ? "pass" : "fail");

        if (newlyBlocked.Count > 0 && plan.PauseOnBlocked)
        {
            NeedsHuman($"checkpoint(s) newly BLOCKED: {string.Join(", ", newlyBlocked)} — see tracker handoff");
            SaveAndReport();
            return;
        }

        if (gatesGreen && (rec.NewCommits.Count > 0 || postTrack.StageDone(stage.Id)) && !agentErrored)
        {
            if (ShouldVerify(rec))
            {
                state.PendingVerify = new PendingVerify
                {
                    FromSession = rec.Number,
                    StageId = stage.Id,
                    StageStartHead = state.CurrentStageStartHead ?? startHead,
                };
                rec.Outcome = SessionOutcome.Progress;
                state.PendingFix = null;
                if (dirty) Log($"note: working tree left dirty after green session: {Git.DirtySummary(plan.Repo)}");
                Log($"session #{rec.Number} Progress — verifier queued to independently check the work");
            }
            else
            {
                rec.Outcome = rec.NewlyDone.Count > 0 ? SessionOutcome.Advanced : SessionOutcome.Progress;
                state.AttemptsThisStage = rec.NewlyDone.Count > 0 ? 0 : state.AttemptsThisStage + 1;
                state.PendingFix = null;
                if (dirty) Log($"note: working tree left dirty after green session: {Git.DirtySummary(plan.Repo)}");
                Log($"session #{rec.Number} {rec.Outcome} — {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) + " done" : "no checkpoint flipped yet")}", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
            }

            if (plan.PerPhaseGates && postTrack.StageDone(stage.Id))
            {
                ScheduleGateOrAudit(stage.Id, state.CurrentStageStartHead ?? startHead);
            }
        }
        else
        {
            rec.Outcome = agentErrored ? SessionOutcome.AgentError : gatesGreen ? SessionOutcome.NoProgress : SessionOutcome.GatesRed;
            state.AttemptsThisStage++;
            var prevSession = state.History.Count >= 2 ? state.History[^2] : null;
            if (plan.Limits.SameFailureCircuitBreaker
                && FailureCircuitBreaker.ShouldBreak(prevSession, rec, gates))
            {
                Log($"circuit breaker: identical failure pattern detected ({rec.Outcome} ×2) — consulting advisor");
                var breakerVerdict = await ConsultAdvisorAsync(rec, stage, _progress.Read(plan, ct),
                    $"identical failure pattern: 2 consecutive {rec.Outcome} sessions with matching symptoms").ConfigureAwait(false);
                await ApplyVerdictAsync(breakerVerdict, rec, stage, defaultAction: AdvisorAction.NeedsHuman).ConfigureAwait(false);
                state.Status = RunStatus.Idle;
                SaveAndReport();
                return;
            }
            state.PendingFix = new PendingFix
            {
                FromSession = rec.Number,
                GateFailures = GateRunner.FailureDetails(gates),
                ProgressSummary = $"new commits: {rec.NewCommits.Count}" +
                                  (rec.NewCommits.Count > 0 ? $" ({string.Join("; ", rec.NewCommits.Take(5))})" : "") +
                                  $" · newly DONE: {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) : "none")}" +
                                  $" · working tree: {(dirty ? "DIRTY — " + Git.DirtySummary(plan.Repo) : "clean")}" +
                                  (agentErrored ? " · agent process reported an error result" : ""),
            };
            Log($"session #{rec.Number} {rec.Outcome} — queuing fix session (attempt {state.AttemptsThisStage}/{MaxAttempts(stage)})", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
        }
        state.Status = RunStatus.Idle;
        SaveAndReport();
    }
}
