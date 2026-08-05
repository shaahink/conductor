namespace Conductor.Core.Http;

public sealed record ControlRequestDto(string? Command, bool Confirmed, string? IntentId, string? StageId, bool Force, string? Value);

public sealed record ControlAcceptedDto(bool Accepted, string? Reason);

public sealed record InjectRequestDto(string? Content, string? StageId);
