using Conductor.Models;

namespace Conductor.Core.Orchestration;

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

    /// <summary>The line the engine writes when it applies this decision, where that line is composed
    /// of evidence alone. Null where it is not — an audit's commit count and a verifier's finding
    /// count belong to objects the decision never sees, and those lines stay at the call site.</summary>
    public string? Log { get; init; }

    /// <summary>Whether the run returns to Idle. NOT redundant with the disposition: the stall-branch
    /// circuit break is the one decision that leaves the status exactly where it was, and until this
    /// field existed that asymmetry was invisible to everything except a careful reader.</summary>
    public bool ReturnToIdle { get; init; }

    public StallBackoffPlan? Backoff { get; init; }
}
