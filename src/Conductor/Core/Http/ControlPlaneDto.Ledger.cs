namespace Conductor.Core.Http;

// M7.1: the knowledge ledger, surfaced to the Face (GET /ledger).

public sealed record LedgerEntryDto(
    long Id, int? SessionNumber, string? StageId, string Kind, string Content, string CreatedAt);

public sealed record LedgerDto(IReadOnlyList<LedgerEntryDto> Entries);
