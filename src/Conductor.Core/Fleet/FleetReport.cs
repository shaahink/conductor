using System.Text.Json.Serialization;

namespace Conductor.Core.Fleet;

/// <summary>What <c>conductor ps --json</c> emits.</summary>
public sealed record FleetReport(DateTime ScannedUtc, string Ports, IReadOnlyList<FleetRunDto> Runs);

/// <summary>The wire shape of one row. Flattened deliberately: a script asking "which port is the
/// sk-studio run on" should not have to know about the enrichment layering.</summary>
public sealed record FleetRunDto(
    string Repo, string PlanName, string RunId, string Status, int Port, int Pid,
    string StageId, string StageTitle, string? AttentionReason,
    int Done, int Total, decimal CostUsd, string BaseUrl, string StateDir,
    DateTime? StartedUtc, bool DiscoveryFile, bool Self);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FleetReport))]
public sealed partial class FleetJsonContext : JsonSerializerContext;
