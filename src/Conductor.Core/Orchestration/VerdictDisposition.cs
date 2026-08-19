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
