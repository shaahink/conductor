namespace Conductor.Core.Events;

public sealed record SessionStarted : ConductorEvent
{
    public int Number { get; init; }
    public required string StageId { get; init; }
    public required string Kind { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public string? AgentSessionId { get; init; }
    public string? Persona { get; init; }
    /// <summary>The model the session's resolved agent actually runs (stage/assignment overrides
    /// applied) — the Face's "what model is working" answer. null on events from older builds.</summary>
    public string? Model { get; init; }
}

public sealed record SessionFinished : ConductorEvent
{
    public int Number { get; init; }
    public required string StageId { get; init; }
    public required string Outcome { get; init; }
    public IReadOnlyList<string> NewCommits { get; init; } = [];
    /// <summary>SC4.3: commits this session landed in the plan's declared satelliteRepos. Carried on
    /// the event so a state rebuilt from the log still knows the session delivered — without it, a
    /// projection replay turns satellite-only work back into the empty history sk #3 misread.</summary>
    public IReadOnlyList<string> SatelliteCommits { get; init; } = [];
    public IReadOnlyList<string> NewlyDone { get; init; } = [];
    public decimal? CostUsd { get; init; }
    public long? TokensInput { get; init; }
    public long? TokensOutput { get; init; }
    public long? TokensReasoning { get; init; }
    public long? TokensCacheRead { get; init; }
}

public sealed record SoftBreakRequested : ConductorEvent
{
    public long LiveTokens { get; init; }
    public long TokenBudget { get; init; }
    public string? CurrentCheckpointId { get; init; }
}

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
