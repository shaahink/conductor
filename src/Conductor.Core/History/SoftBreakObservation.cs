namespace Conductor.Core.History;

/// <summary>
/// K4.2 — one firing of the cooperative nudge, recovered from the archived event log.
/// <para>This is the measurement that makes a budget analysis honest. Everything else about a cap has
/// to be inferred: <c>runs.limits</c> only exists from schema v11, the plan file on disk is whatever
/// it was edited to last, and a ratio quoted in a doc is a claim. A <c>SoftBreakRequested</c> event
/// carries <c>liveTokens</c> and <c>tokenBudget</c> as they were AT THE MOMENT THE RAIL FIRED, so a
/// run that ever nudged states its own ceiling and its own nudge point, per session, forever.</para>
/// <para>The event's <c>session_id</c> column is NULL on every row this repo has (the emitter does not
/// stamp it), so attribution walks back to the nearest preceding <c>SessionStarted</c> by sequence —
/// which is exact, because a session's events cannot interleave with another session's.</para>
/// </summary>
/// <param name="Session">The session number the nudge was delivered to.</param>
/// <param name="LiveTokens">Agent tokens spent when the rail fired — the nudge point, measured.</param>
/// <param name="TokenBudget">The ceiling in force at that moment. Null on a malformed payload.</param>
/// <param name="Checkpoint">The checkpoint the session was on, if the payload named one.</param>
public sealed record SoftBreakObservation(int Session, long LiveTokens, long? TokenBudget, string? Checkpoint);
