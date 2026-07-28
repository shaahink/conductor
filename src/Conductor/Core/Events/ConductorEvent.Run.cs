namespace Conductor.Core.Events;

public sealed record RunStarted : ConductorEvent
{
    public required string Plan { get; init; }
    public required string Repo { get; init; }
    public string? Branch { get; init; }
    public string? DriverVersion { get; init; }
    public bool Resumed { get; init; }
}

public sealed record RunFinished : ConductorEvent
{
    public required string Status { get; init; }
    public int Sessions { get; init; }
    public int CheckpointsDone { get; init; }
    public int CheckpointsTotal { get; init; }
}

public sealed record CheckpointConfirmed : ConductorEvent
{
    public required string CheckpointId { get; init; }
    public required string StageId { get; init; }
}
