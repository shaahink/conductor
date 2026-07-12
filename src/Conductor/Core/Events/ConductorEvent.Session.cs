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
}

public sealed record SessionFinished : ConductorEvent
{
    public int Number { get; init; }
    public required string StageId { get; init; }
    public required string Outcome { get; init; }
    public IReadOnlyList<string> NewCommits { get; init; } = [];
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
