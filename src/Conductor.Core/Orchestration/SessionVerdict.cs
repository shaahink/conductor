using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS6.4 — the pure evidence-to-verdict function. Total, deterministic and allocation-only: given the
/// same <see cref="SessionEvidence"/> it returns the same <see cref="VerdictDecision"/>, on any
/// machine, at any time, with no run in progress. The taxonomy is therefore testable without the
/// loop, which is the whole point — before this, every branch below could only be reached by standing
/// up a RunContext, a store, a git repository and an agent process.
/// </summary>
public static class SessionVerdict
{
    /// <summary>Judge a session from its evidence. Returns a continuation
    /// (<see cref="VerdictDisposition.RunGateBattery"/>, <see cref="VerdictDisposition.ReadWorkEvidence"/>,
    /// or <see cref="VerdictDisposition.HonourBlockUntil"/> when the wait turns out to be stale) when
    /// the caller must buy more evidence and ask again.</summary>
    public static VerdictDecision Decide(SessionEvidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.WorkEvidenceRead ? Delivery(e)
             : e.GatesRun ? AfterGates(e)
             : Triage(e);
    }

    // ── pass 1: is there work to grade at all? Everything returning here is a battery not bought ──

    private static VerdictDecision Triage(SessionEvidence e)
    {
        if (e.KilledByUser)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.PauseKilled,
                Outcome = SessionOutcome.KilledByUser,
                Log = "session killed by user — pausing (conductor resume to continue)",
            };
        }

        if (e.Stalled || e.TimedOut) return StallOrTimeout(e);

        // The healthy path clears the multiplier and deliberately leaves the instant alone.
        var settled = new StallBackoffPlan(1, null, TouchesUntil: false);

        if (e.BlockedUntilRequested)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.HonourBlockUntil,
                Backoff = settled,
            };
        }

        if (e.Kind == SessionKind.Audit)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.AuditComplete,
                Outcome = SessionOutcome.Progress,
                ReturnToIdle = true,
                Backoff = settled,
            };
        }

        return e.Kind == SessionKind.Verify
            ? Verify(e, settled)
            : new VerdictDecision { Disposition = VerdictDisposition.RunGateBattery, Backoff = settled };
    }

    private static VerdictDecision StallOrTimeout(SessionEvidence e)
    {
        var outcome = e.Stalled ? SessionOutcome.Stalled : SessionOutcome.TimedOut;

        // The breaker returns BEFORE the backoff bookkeeping and without touching the status. Both of
        // those were accidents of statement order; both are behaviour, and both are pinned now.
        if (e.CircuitBreakerEnabled && e.SameFailurePattern)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.ConsultAdvisor,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                AdvisorDefault = AdvisorAction.NeedsHuman,
                Reason = $"identical failure pattern: 2 consecutive {outcome} sessions with matching symptoms",
                Log = $"circuit breaker: identical failure pattern detected ({outcome} ×2) — consulting advisor",
            };
        }

        if (e.Stalled && e.StallPatternTerminationEnabled && !e.CircuitBreakerEnabled && e.IdenticalStallPattern)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.ParkForHuman,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                Reason = $"identical-stall: {e.SessionNumber - 1} sessions stalled with no commits, no output — environment or agent is broken",
            };
        }

        var multiplier = e.Stalled ? e.PriorStallBackoffMultiplier + 1 : 1;
        var backoff = new StallBackoffPlan(
            multiplier,
            e.Stalled ? e.StallBackoffMinutes * multiplier : null,
            TouchesUntil: true);

        return e.ResumeCount < e.MaxResumesPerSession
            ? new VerdictDecision
            {
                Disposition = VerdictDisposition.Resume,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                Reason = e.Stalled ? "session stalled (no output)" : "session hit the hard timeout",
                Log = $"will resume agent session (resume {e.ResumeCount + 1}/{e.MaxResumesPerSession})",
                ReturnToIdle = true,
                Backoff = backoff,
            }
            : new VerdictDecision
            {
                Disposition = VerdictDisposition.ConsultAdvisor,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                AdvisorDefault = AdvisorAction.Retry,
                Reason = "resume budget exhausted after stall/timeout",
                ReturnToIdle = true,
                Backoff = backoff,
            };
    }

    private static VerdictDecision Verify(SessionEvidence e, StallBackoffPlan settled)
    {
        if (!e.VerifierParsed)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.VerifyUnparseable,
                Outcome = SessionOutcome.AgentError,
                Attempts = AttemptEffect.Increment,
                Log = "verifier produced no parseable score — treating as agent error, queuing fix",
                ReturnToIdle = true,
                Backoff = settled,
            };
        }

        return e.VerifierScore >= e.VerifierThreshold
            ? new VerdictDecision
            {
                Disposition = VerdictDisposition.VerifyPassed,
                Outcome = SessionOutcome.Progress,
                Attempts = AttemptEffect.Reset,
                ReturnToIdle = true,
                Backoff = settled,
            }
            : new VerdictDecision
            {
                Disposition = VerdictDisposition.VerifyFailed,
                Outcome = SessionOutcome.NoProgress,
                Attempts = AttemptEffect.Increment,
                Reason = $"verifier score {e.VerifierScore}/100 < threshold {e.VerifierThreshold}",
                ReturnToIdle = true,
                Backoff = settled,
            };
    }

    // ── pass 2: the battery is in ──

    private static VerdictDecision AfterGates(SessionEvidence e) =>
        e.Cancelled
            ? new VerdictDecision
            {
                Disposition = VerdictDisposition.Interrupted,
                Outcome = SessionOutcome.Interrupted,
                Reason = "conductor was cancelled during gate verification",
                Log = "verification interrupted — will re-verify on resume (no fix queued)",
                ReturnToIdle = true,
            }
            : new VerdictDecision { Disposition = VerdictDisposition.ReadWorkEvidence };

    // ── pass 3: the work verdict ──

    private static VerdictDecision Delivery(SessionEvidence e)
    {
        if (e.NewlyBlocked.Count > 0 && e.PauseOnBlocked)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.ParkForHuman,
                Reason = $"checkpoint(s) newly BLOCKED: {string.Join(", ", e.NewlyBlocked)} — see tracker handoff",
            };
        }

        // SC4.2/SC4.3: NoProgress has to mean no progress. A checkpoint claimed through the work graph
        // IS delivery even when this repo's git log is empty, and so is a commit in a declared satellite.
        var delivered = e.WorkCommitCount > 0 || e.NewlyDoneCount > 0 || e.StageComplete;
        // KS4.2: read separately from GatesGreen on purpose. A regression already turns that false in
        // the runner, so this changes no verdict on its own — it is here so that the ONE path to
        // Deliver is guarded by the regression class explicitly, and stays guarded if a later change
        // ever lets a regressing battery report green.
        var regressed = e.Regressions.Count > 0;
        if (e.GatesGreen && !regressed && delivered && !e.AgentErrored)
        {
            return new VerdictDecision
            {
                Disposition = VerdictDisposition.Deliver,
                Outcome = e.NewlyDoneCount > 0 ? SessionOutcome.Advanced : SessionOutcome.Progress,
                Attempts = e.NewlyDoneCount > 0 ? AttemptEffect.Reset : AttemptEffect.Unchanged,
                ReturnToIdle = true,
            };
        }

        var outcome = e.AgentErrored ? SessionOutcome.AgentError
                    : regressed ? SessionOutcome.GatesRed
                    : e.GatesGreen ? SessionOutcome.NoProgress
                    : SessionOutcome.GatesRed;

        return e.CircuitBreakerEnabled && e.SameFailurePattern
            ? new VerdictDecision
            {
                Disposition = VerdictDisposition.ConsultAdvisor,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                AdvisorDefault = AdvisorAction.NeedsHuman,
                Reason = $"identical failure pattern: 2 consecutive {outcome} sessions with matching symptoms",
                Log = $"circuit breaker: identical failure pattern detected ({outcome} ×2) — consulting advisor",
                ReturnToIdle = true,
            }
            : new VerdictDecision
            {
                Disposition = VerdictDisposition.QueueFix,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                Reason = regressed ? RegressionReason(e.Regressions) : "",
                Log = regressed ? "verdict: " + RegressionReason(e.Regressions) : null,
                ReturnToIdle = true,
            };
    }

    /// <summary>KS4.2: the verdict said in the class's own words. "a gate failed" sends a fix session
    /// looking for a failing assertion that does not exist — the gate exited 0.</summary>
    private static string RegressionReason(IReadOnlyList<RegressionEvidence> rows)
        => "regression class (PASS-TO-PASS): " + string.Join("; ", rows.Select(r => r.Note is { } note
            ? $"gate '{r.Gate}' {note}"
            : $"gate '{r.Gate}' passed but {r.BrokenChecks.Count} check(s) that passed earlier in this run no longer " +
              $"pass: {string.Join(", ", r.BrokenChecks.Take(5))}{(r.BrokenChecks.Count > 5 ? $" (and {r.BrokenChecks.Count - 5} more)" : "")}"));
}
