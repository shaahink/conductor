using System.Text.Json.Serialization;

namespace Conductor.Core.History;

/// <summary>
/// K3.2: the machine-readable shape of <c>conductor history</c>. A separate contract from the
/// records the archive returns, for the same reason <c>FleetRunDto</c> is separate from
/// <c>FleetRun</c>: what goes to stdout is a promise to whoever parses it, and it must not change
/// shape because an internal record grew a field.
/// </summary>
public sealed record RunHistoryListJson(IReadOnlyList<RunHistoryItemJson> Runs);

/// <summary>One run in the listing. <paramref name="Readable"/> false means the catalogue names a
/// database that is no longer there; every other field but the provenance ones is then empty.</summary>
public sealed record RunHistoryItemJson(
    string RunId, string Repo, string Plan, string Status,
    string? Engine, string? Branch,
    string? StartedUtc, string? EndedUtc, string? LastActivityUtc,
    int Sessions, int CheckpointsDone, int CheckpointsTotal,
    decimal CostUsd, long Tokens,
    string RunDb, string Slug, string? ImportedFrom, bool Readable);

/// <summary>One run opened: the row, plus its spine.</summary>
public sealed record RunHistoryDetailJson(
    RunHistoryItemJson Run,
    IReadOnlyList<ArchivedStage> Stages,
    IReadOnlyList<ArchivedCheckpoint> Checkpoints,
    IReadOnlyList<ArchivedSession> Sessions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunHistoryListJson))]
[JsonSerializable(typeof(RunHistoryDetailJson))]
public sealed partial class RunHistoryJsonContext : JsonSerializerContext;
