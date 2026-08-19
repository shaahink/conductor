using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS6.4 — what the engine decided about a finished session, with nothing about how the decision is
/// carried out. Two of these are continuations rather than verdicts (<see cref="RunGateBattery"/>,
/// <see cref="ReadWorkEvidence"/>): they are the points where the loop must go and buy more evidence
/// before <see cref="SessionVerdict.Decide"/> can settle anything, and naming them is what lets one
/// total function cover a method that used to interleave judgement with three rounds of I/O.
/// </summary>
public enum VerdictDisposition
{
    /// <summary>Not a judgement about the work at all: the operator killed the session. The run pauses.</summary>
    PauseKilled,

    /// <summary>SC5.1: the session asked to wait until a known instant. The caller attempts to honour
    /// it and, if it cannot because the window has already opened, re-decides with
    /// <see cref="SessionEvidence.BlockedUntilRequested"/> cleared — a stale wait is not a reason to
    /// skip judging the session.</summary>
    HonourBlockUntil,

    /// <summary>Resume the same agent session rather than starting a new one.</summary>
    Resume,

    /// <summary>Hand the situation to the advisor, falling back to
    /// <see cref="VerdictDecision.AdvisorDefault"/> when it returns nothing usable.</summary>
    ConsultAdvisor,

    /// <summary>Stop and ask a human. <see cref="VerdictDecision.Reason"/> is the sentence they read.</summary>
    ParkForHuman,

    /// <summary>An audit session finished: re-verify the phase with the full battery.</summary>
    AuditComplete,

    /// <summary>A verify session scored at or above the effective threshold.</summary>
    VerifyPassed,

    /// <summary>A verify session scored below the effective threshold: queue a fix.</summary>
    VerifyFailed,

    /// <summary>A verify session produced no parseable score. Agent error, and a fix is queued.</summary>
    VerifyUnparseable,

    /// <summary>CONTINUATION, not a verdict: nothing above settled it, so the engine must pay for the
    /// gate battery and decide again with <see cref="SessionEvidence.GatesRun"/> set. Every early
    /// return above this line is a battery the run does not buy.</summary>
    RunGateBattery,

    /// <summary>The battery was cancelled mid-flight: re-verify on resume, queue no fix, burn nothing.</summary>
    Interrupted,

    /// <summary>CONTINUATION: gates are in and nothing aborted, so read the tracker and commit evidence
    /// and decide again with <see cref="SessionEvidence.WorkEvidenceRead"/> set.</summary>
    ReadWorkEvidence,

    /// <summary>Green: the session delivered. <see cref="VerdictDecision.Outcome"/> separates a
    /// checkpoint that flipped (Advanced) from work that landed without one (Progress).</summary>
    Deliver,

    /// <summary>Red: queue a fix session.</summary>
    QueueFix,
}

/// <summary>What a decision does to <c>AttemptsThisStage</c>. Spelled out because the increment used
/// to happen in five places and be skipped in two, and no test could see the difference.</summary>
public enum AttemptEffect
{
    /// <summary>Leave the counter alone.</summary>
    Unchanged,

    /// <summary>Spend an attempt.</summary>
    Increment,

    /// <summary>Give the stage its attempts back — progress was made.</summary>
    Reset,
}

/// <summary>
/// What the decision does to the stall backoff. <see cref="Multiplier"/> is always applied;
/// <see cref="DelayMinutes"/> is applied only when <see cref="TouchesUntil"/> is set, because the
/// fall-through reset on a healthy session resets the multiplier and deliberately leaves the instant
/// where it was.
/// </summary>
public sealed record StallBackoffPlan(int Multiplier, int? DelayMinutes, bool TouchesUntil);

/// <summary>
/// KS4.5's seam. An advisory row is evidence produced by a judgement rather than a measurement — a
/// second-model review, a heuristic score, anything whose author is not a deterministic gate. It is
/// recorded with the rest of the taxonomy and it is <em>never read by
/// <see cref="SessionVerdict.Decide"/></em>. That is not a convention: it is asserted twice, once
/// behaviourally over the whole decision table and once as a source rule.
/// </summary>
public sealed record AdvisoryEvidence(string Source, string Verdict, int? Score, string Detail);

/// <summary>
/// The evidence taxonomy, as data. Every row a session verdict has ever rested on, and nothing else —
/// no run context, no store, no repository, no clock. Filled in three passes, marked by
/// <see cref="GatesRun"/> and <see cref="WorkEvidenceRead"/>, because the engine buys gate evidence
/// and tracker evidence only when the cheaper rows have not already settled the session.
/// </summary>
public sealed record SessionEvidence
{
    // ── phase markers: which rows are filled in yet ──

    /// <summary>The gate battery has run (or was skipped by override) and <see cref="GatesGreen"/> is real.</summary>
    public bool GatesRun { get; init; }

    /// <summary>The tracker diff and commit collection have run, so the work rows are real.</summary>
    public bool WorkEvidenceRead { get; init; }

    // ── control rows: about the SESSION, not about the work ──

    public SessionKind Kind { get; init; }

    public int SessionNumber { get; init; }

    public bool KilledByUser { get; init; }

    public bool Stalled { get; init; }

    public bool TimedOut { get; init; }

    public bool AgentErrored { get; init; }

    /// <summary>Cancellation arrived while the battery was running. Only meaningful once
    /// <see cref="GatesRun"/> is set — before that the run had not started paying for anything.</summary>
    public bool Cancelled { get; init; }

    /// <summary>SC5.1: the session emitted a still-unhonoured <c>blocked-until</c> request.</summary>
    public bool BlockedUntilRequested { get; init; }

    // ── resume and backoff rows ──

    public int ResumeCount { get; init; }

    public int MaxResumesPerSession { get; init; }

    public int PriorStallBackoffMultiplier { get; init; } = 1;

    public int StallBackoffMinutes { get; init; }

    public bool StallPatternTerminationEnabled { get; init; }

    public bool IdenticalStallPattern { get; init; }

    public bool CircuitBreakerEnabled { get; init; }

    /// <summary>The same-failure detector fired. Gathered only when
    /// <see cref="CircuitBreakerEnabled"/>, mirroring the short-circuit it replaced.</summary>
    public bool SameFailurePattern { get; init; }

    // ── work rows: the deterministic evidence a delivery verdict is made of ──

    public bool GatesGreen { get; init; }

    public int WorkCommitCount { get; init; }

    public int NewlyDoneCount { get; init; }

    public IReadOnlyList<string> NewlyBlocked { get; init; } = [];

    public bool PauseOnBlocked { get; init; }

    public bool StageComplete { get; init; }

    /// <summary>Recorded because the "verdict inputs" line reports it. A dirty working tree has never
    /// changed a verdict and does not change one here — it is a note in the log and in the fix brief.</summary>
    public bool WorkingTreeDirty { get; init; }

    // ── verifier rows ──

    public bool VerifierParsed { get; init; }

    public int VerifierScore { get; init; }

    public int VerifierThreshold { get; init; }

    // ── KS4.5: advisory rows. Recorded, never consulted. ──

    /// <summary>Evidence that came from a judgement rather than a measurement. See
    /// <see cref="AdvisoryEvidence"/>: nothing in <see cref="SessionVerdict.Decide"/> reads this.</summary>
    public IReadOnlyList<AdvisoryEvidence> AdvisoryRows { get; init; } = [];
}

/// <summary>What to do about the session, and why.</summary>
public sealed record VerdictDecision
{
    public required VerdictDisposition Disposition { get; init; }

    /// <summary>The outcome to stamp on the session record, or null for the continuations, which
    /// settle nothing.</summary>
    public SessionOutcome? Outcome { get; init; }

    public AttemptEffect Attempts { get; init; } = AttemptEffect.Unchanged;

    /// <summary>Where the advisor lands when it returns nothing usable. Read only for
    /// <see cref="VerdictDisposition.ConsultAdvisor"/>.</summary>
    public AdvisorAction AdvisorDefault { get; init; } = AdvisorAction.Retry;

    /// <summary>The sentence handed to the advisor, the human or the resume queue. Composed here so
    /// the wording is something the decision table pins rather than an accident of a call site.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Whether the run returns to Idle. NOT redundant with the disposition: the stall-branch
    /// circuit break is the one decision that leaves the status exactly where it was, and until this
    /// field existed that asymmetry was invisible to everything except a careful reader.</summary>
    public bool ReturnToIdle { get; init; }

    public StallBackoffPlan? Backoff { get; init; }
}

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
                Reason = "session killed by user — pausing (conductor resume to continue)",
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
                Reason = "verifier produced no parseable score — treating as agent error, queuing fix",
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
        if (e.GatesGreen && delivered && !e.AgentErrored)
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
                ReturnToIdle = true,
            }
            : new VerdictDecision
            {
                Disposition = VerdictDisposition.QueueFix,
                Outcome = outcome,
                Attempts = AttemptEffect.Increment,
                ReturnToIdle = true,
            };
    }
}
