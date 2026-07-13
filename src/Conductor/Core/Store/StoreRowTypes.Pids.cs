namespace Conductor.Core.Store;

public sealed record PidRow(
    int Pid,
    string Purpose,
    string? StageId,
    int? SessionNumber,
    DateTime StartedUtc,
    DateTime? ExitedUtc,
    int? ExitCode,
    string RunId);
