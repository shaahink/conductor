namespace Conductor.Core.Watch;

/// <summary>
/// SF5.1 — the closed set of things a supervisor is allowed to be woken for.
///
/// <para>The owner's diagnosis of the polling babysitter was precise: over ten hours ~95% of ticks
/// say "still running", and the cost is not the polls, it is the <em>accumulation</em> — every tick
/// lands in a context that is paid for again on the next one. So the wake set is small on purpose,
/// and the DON'T-wake set is the load-bearing half: usage-limit backoff, stall backoff, session
/// start/exit, rollover, gate PASS, phase advance and an agent-declared blocked-until nap all
/// resolve themselves, and two of the last three events on a real run were exactly that.</para>
///
/// <para><see cref="Timeout"/> is not a wake — it is the long-fallback heartbeat, so a shell loop
/// survives an engine that hangs without ever emitting anything. It returns a different exit code
/// and, deliberately, does NOT fire the supervisor hook.</para>
/// </summary>
public enum WatchReason
{
    /// <summary>The run parked at NeedsHuman (advisor verdict, backoff cap, blocked-not-converging,
    /// exhausted attempts, a HUMAN: line in the handoff — every one of them lands here).</summary>
    NeedsHuman,

    /// <summary>The run parked awaiting the owner. Split into <c>owner-gate</c> vs <c>budget-park</c>
    /// by <see cref="WatchBrief"/>, which has the run state in hand; the event alone cannot tell
    /// them apart because both emit <c>ownerApprovalRequested</c>.</summary>
    OwnerPark,

    /// <summary>Two consecutive sessions on one stage ended with the same breakable failure —
    /// the churn loop the watch-run skill's rule 2 exists for.</summary>
    CircuitBreaker,

    /// <summary>A stage's full-battery phase gate came back RED twice. Once is a normal fix loop;
    /// twice on the same stage is the pattern that eats an attempt budget.</summary>
    PhaseRedTwice,

    /// <summary>An engine that WAS holding the lock is not running any more — a crash or a closed
    /// window, which otherwise looks exactly like a quiet run.</summary>
    EngineGone,

    /// <summary>The run finished (completed, aborted, or stopped at the session cap).</summary>
    RunEnded,

    /// <summary>--timeout expired with nothing on the wake set. The heartbeat, not a wake.</summary>
    Timeout,
}

/// <summary>What fired, in the words the brief will carry. <paramref name="Seq"/> is the event-log
/// ordinal that triggered it (0 for the two triggers that are not events: liveness and timeout).</summary>
public sealed record WatchWake(WatchReason Reason, string Detail, string? StageId = null, long Seq = 0)
{
    /// <summary>Where the trigger came from: <c>event</c>, <c>state</c> (the condition was already
    /// true when the watch was armed), <c>liveness</c>, or <c>timeout</c>. A supervisor reads this to
    /// tell "this just happened" from "this had already happened before I looked".</summary>
    public string FiredFrom { get; init; } = "event";
}
