namespace Conductor.Core.Http;

public sealed record QueryResultDto(IReadOnlyList<string> Columns, IReadOnlyList<QueryRowDto> Rows, bool Truncated, string? Error);

public sealed record ControlPlaneInfo(int Port, string BaseUrl, int Pid, string PlanName, DateTime StartedUtc);
