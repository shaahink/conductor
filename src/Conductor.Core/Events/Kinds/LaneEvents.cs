namespace Conductor.Core.Events;

public sealed record LaneStarted : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public string? StageId { get; init; }
}

public sealed record LaneFinished : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
}

public sealed record MutatingLaneStarted : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public string? StageId { get; init; }
    public string? ScratchBranch { get; init; }
}
