namespace Conductor.Core.Http;

public sealed record InjectAcceptedDto(bool Accepted, string? Reason, string? RunId, string? StageId, string? RecordedUtc);

public sealed record ProcessDto(
    int Pid, string Purpose, string? StageId, int? SessionNumber,
    string StartedUtc, string? ExitedUtc, int? ExitCode, bool Alive, string? LastOutputLine);

public sealed record ProcessesDto(IReadOnlyList<ProcessDto> Processes);
