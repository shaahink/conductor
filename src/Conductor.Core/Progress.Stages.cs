namespace Conductor.Core;

public sealed record GateProgress(string Name, string State, TimeSpan Elapsed, DateTime? StartUtc = null)
{
    public static GateProgress Pending(string name) => new(name, "pending", TimeSpan.Zero);

    public TimeSpan LiveElapsed(DateTime nowUtc)
        => State == "running" && StartUtc is { } s ? nowUtc - s : Elapsed;
}

public sealed record StageProgress
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public string State { get; init; } = "todo";
    public int Attempts { get; init; }
    public string LastOutcome { get; init; } = "";
    public decimal CostUsd { get; init; }
    public string? ParentId { get; init; }
    public int Depth { get; init; }
    public IReadOnlyList<(string Id, string Title, string Status)> Checkpoints { get; init; }
        = Array.Empty<(string, string, string)>();
}
