using Conductor.Planning;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS4.5's seam. An advisory row is evidence produced by a judgement rather than a measurement — a
/// second-model review, a heuristic score, anything whose author is not a deterministic gate. It is
/// recorded with the rest of the taxonomy and it is <em>never read by
/// <see cref="SessionVerdict.Decide"/></em>. That is not a convention: it is asserted twice, once
/// behaviourally over the whole decision table and once as a source rule.
/// </summary>
public sealed record AdvisoryEvidence(string Source, string Verdict, int? Score, string Detail);

/// <summary>
/// KS4.2 — one regression-class gate's finding: checks that passed earlier in this run and do not
/// pass now. A row exists only when something was actually lost, so a non-empty
/// <see cref="SessionEvidence.Regressions"/> IS the regression.
/// </summary>
/// <remarks>This is a measurement, not a judgement, so unlike <see cref="AdvisoryEvidence"/> it is
/// read by <see cref="SessionVerdict.Decide"/> — and it is read separately from
/// <see cref="SessionEvidence.GatesGreen"/> even though a regression already turns that false. The
/// separate row is what lets the verdict SAY "regression" instead of "a gate failed", which for this
/// class is the difference between a fix session that looks for a failing assertion and one that
/// looks for the check that went missing.</remarks>
public sealed record RegressionEvidence(string Gate, IReadOnlyList<string> BrokenChecks, string? Note);

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

    /// <summary>KS4.2: what the regression class found, per gate. Empty on every ordinary battery.
    /// Filled with <see cref="GatesGreen"/>, from the same battery, in the same pass.</summary>
    public IReadOnlyList<RegressionEvidence> Regressions { get; init; } = [];

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
