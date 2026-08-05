namespace Conductor.Core.Http;

/// <summary>The answer to <c>POST /inject</c>. K2.3 moved it here from the file it shared with the
/// process DTOs — "Responses" was a filing cabinet, not a responsibility: an injected instruction and
/// a list of pids have nothing to do with each other beyond both being replies.</summary>
public sealed record InjectAcceptedDto(bool Accepted, string? Reason, string? RunId, string? StageId, string? RecordedUtc);
