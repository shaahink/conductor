namespace Conductor.Core.Events;

public sealed record GateFinished : ConductorEvent
{
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public bool Skipped { get; init; }
    public bool Optional { get; init; }
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    public string? Scope { get; init; }
}

public sealed record TokenDelta : ConductorEvent
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long Reasoning { get; init; }
    public long CacheRead { get; init; }
    public decimal CostUsd { get; init; }
}

public sealed record McpCallFinished : ConductorEvent
{
    public required string ToolName { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
}
