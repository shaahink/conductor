namespace Conductor.Core.Http;

public sealed record QueryResultDto(IReadOnlyList<string> Columns, IReadOnlyList<QueryRowDto> Rows, bool Truncated, string? Error);

/// <summary><c>Token</c> is the per-run write token clients must send as <c>X-Conductor-Token</c>
/// on every POST; the discovery file's filesystem permissions are its trust boundary.</summary>
public sealed record ControlPlaneInfo(int Port, string BaseUrl, int Pid, string PlanName, DateTime StartedUtc, string? Token = null);
