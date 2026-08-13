using System.Text.Json.Serialization;

namespace Conductor.Commands;

/// <summary>The <c>--json</c> shape of a catalogue survey. Stable: the evidence pipeline quotes it.</summary>
public sealed record CatalogueJson(
    string Root,
    int Stores,
    int RunRows,
    int DistinctRuns,
    IReadOnlyList<DuplicateJson> Duplicates,
    IReadOnlyList<string> Deferred,
    bool Applied,
    string? BackupDir,
    int RowsDeleted,
    IReadOnlyList<string> StoresChanged);

/// <summary>One run that lives in more than one store.</summary>
public sealed record DuplicateJson(
    string RunId,
    string Plan,
    string OwnerDb,
    string OwnerReason,
    IReadOnlyList<string> RemoveFrom);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogueJson))]
internal sealed partial class CatalogueJsonContext : JsonSerializerContext;
