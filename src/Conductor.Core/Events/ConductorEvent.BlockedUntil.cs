namespace Conductor.Core.Events;

// SC5.1's two wait events, split out of ConductorEvent.Session.cs: that file reached five types and
// the architecture ratchet allows three. They belong together and apart from the session lifecycle —
// one is a request made from inside a live session, the other is the engine's answer to it.

/// <summary>SC5.1: a session said "I cannot proceed until T, because R" — emitted from the AGENT's
/// own process (`conductor task --blocked-until`, MCP `task_blocked_until`) into the run's event log
/// while the session is still alive. It is a request, not a verdict: the run loop reads it after the
/// session exits and decides, which is what <see cref="RunBlockedUntil"/> records.</summary>
public sealed record BlockedUntilRequested : ConductorEvent
{
    public DateTimeOffset UntilUtc { get; init; }
    public required string Reason { get; init; }
    public string? StageId { get; init; }
    /// <summary>agent | human — who asked for the wait.</summary>
    public string? Source { get; init; }
}

/// <summary>SC5.1: the engine ACCEPTED a wait and is now sleeping on it — the park event, emitted
/// after the session's <see cref="SessionFinished"/> so it is the last thing in the log and every
/// surface that asks "what is happening" gets "waiting", not "idle".</summary>
public sealed record RunBlockedUntil : ConductorEvent
{
    public DateTimeOffset UntilUtc { get; init; }
    public required string Reason { get; init; }
    public string? StageId { get; init; }
    /// <summary>The session that asked for the wait.</summary>
    public int FromSession { get; init; }
}
