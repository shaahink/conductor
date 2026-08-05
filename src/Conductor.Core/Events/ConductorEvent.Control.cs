namespace Conductor.Core.Events;

public sealed record AttentionRequested : ConductorEvent
{
    public required string Reason { get; init; }
}

public sealed record OwnerApprovalRequested : ConductorEvent
{
    public required string StageId { get; init; }
}

public sealed record OwnerApprovalGranted : ConductorEvent
{
    public required string StageId { get; init; }
}
