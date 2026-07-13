namespace Conductor.Core.Http;

public sealed record TimelineEntryDto(
    string Utc, string Kind, string Description, string? StageId, int? SessionNumber,
    decimal? CostUsd, string? Outcome);

public sealed record TimelineDto(IReadOnlyList<TimelineEntryDto> Entries);
