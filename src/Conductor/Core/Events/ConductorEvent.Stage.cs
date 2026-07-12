namespace Conductor.Core.Events;

public sealed record StageEntered : ConductorEvent
{
    public required string StageId { get; init; }
    public string? Title { get; init; }
    public string? StartHead { get; init; }
}

public sealed record StageConfirmed : ConductorEvent
{
    public required string StageId { get; init; }
    public bool Audited { get; init; }
}

public sealed record RollbackExecuted : ConductorEvent
{
    public required string StageId { get; init; }
    public required string FromSha { get; init; }
    public required string ToSha { get; init; }
    public bool Forced { get; init; }
}
