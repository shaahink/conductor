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

    /// <summary>KS7.3 — of <see cref="Input"/>, how many were cache WRITES. A subset of Input, never a
    /// peer: adding it to a total that already contains Input double-counts. Zero on every event
    /// written before this checkpoint and on providers whose wire does not report the split, which is
    /// why a consumer must treat 0 as "not reported" rather than "no cache was written".</summary>
    public long CacheWrite { get; init; }

    public decimal CostUsd { get; init; }
}

public sealed record McpCallFinished : ConductorEvent
{
    public required string ToolName { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
}
