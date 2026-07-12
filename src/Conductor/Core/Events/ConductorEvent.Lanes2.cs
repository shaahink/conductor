namespace Conductor.Core.Events;

public sealed record MutatingLaneFinished : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public bool AgentCommitted { get; init; }
}

public sealed record MergeGateVerdict : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public bool Passed { get; init; }
    public int TotalGates { get; init; }
    public int PassedCount { get; init; }
    public int FailedCount { get; init; }
    public string? FailureSummary { get; init; }
    public long DurationMs { get; init; }
}
