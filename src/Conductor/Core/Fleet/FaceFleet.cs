using System.Text.Json.Serialization;

namespace Conductor.Core.Fleet;

/// <summary>The <c>CONDUCTOR_FLEET</c> envelope <see cref="FaceTarget"/> hands the Face. Mirrored by
/// <c>tui.Fleet</c> in face-go.</summary>
public sealed record FaceFleet(IReadOnlyList<FaceFleetRun> Runs);

/// <summary>One attachable run as the Face sees it. Identical to <see cref="FleetRunDto"/> plus
/// <paramref name="Token"/> — and the token is the whole reason this is a separate type: the shape
/// that goes to stdout must not be able to grow one by accident.</summary>
public sealed record FaceFleetRun(
    string Repo, string PlanName, string RunId, string Status, int Port, int Pid,
    string StageId, string StageTitle, string? AttentionReason,
    int Done, int Total, decimal CostUsd, string BaseUrl, string StateDir,
    string? Token, bool Self);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FaceFleet))]
public sealed partial class FaceFleetJsonContext : JsonSerializerContext;
