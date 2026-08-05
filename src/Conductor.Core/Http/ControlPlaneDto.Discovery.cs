namespace Conductor.Core.Http;

// This file was ControlPlaneDto.Query.cs until SF1.2, when QueryResultDto died with GET /report/query.
// What is left is the discovery file's wire type, which never had anything to do with SQL.

/// <summary><c>Token</c> is the per-run write token clients must send as <c>X-Conductor-Token</c>
/// on every POST; the discovery file's filesystem permissions are its trust boundary.</summary>
public sealed record ControlPlaneInfo(int Port, string BaseUrl, int Pid, string PlanName, DateTime StartedUtc, string? Token = null);
