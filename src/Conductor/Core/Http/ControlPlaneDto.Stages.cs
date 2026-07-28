namespace Conductor.Core.Http;

public sealed record CheckpointDto(string Id, string Title, string Status);

public sealed record StageDto(
    string Id, string Title, int Done, int Total, string State,
    int Attempts, string LastOutcome, decimal CostUsd, string? ParentId, int Depth,
    IReadOnlyList<CheckpointDto> Checkpoints);

public sealed record GateDto(string Name, string State, double ElapsedSec);
