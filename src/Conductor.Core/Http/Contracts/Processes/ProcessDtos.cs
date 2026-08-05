namespace Conductor.Core.Http;

/// <summary>One row of <c>GET /processes</c>: a pid this run started, what for, and whether it is
/// still alive. <c>LastOutputLine</c> is the tail the Face shows without opening the log.</summary>
public sealed record ProcessDto(
    int Pid, string Purpose, string? StageId, int? SessionNumber,
    string StartedUtc, string? ExitedUtc, int? ExitCode, bool Alive, string? LastOutputLine);

public sealed record ProcessesDto(IReadOnlyList<ProcessDto> Processes);
