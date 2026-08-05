using System.Text.Json.Serialization;

namespace Conductor.Core.Fleet;

/// <summary>The <c>CONDUCTOR_FLEET</c> envelope <see cref="FaceTarget"/> hands the Face. Mirrored by
/// <c>tui.Fleet</c> in face-go.</summary>
public sealed record FaceFleet(IReadOnlyList<FaceFleetRun> Runs)
{
    /// <summary>K3.2: runs this machine REMEMBERS but is not serving — read from the state
    /// catalogue, not from a port. They are listed under the live ones in the picker and cannot be
    /// attached to, because there is no control plane behind a finished run. An init property rather
    /// than a second positional parameter so every existing caller keeps compiling with an empty
    /// history, which is the correct answer on a machine that has never had one.</summary>
    public IReadOnlyList<FacePastRun> Past { get; init; } = [];
}

/// <summary>One finished run as the picker lists it. No base url and no token: there is nothing to
/// attach to and nothing to authorise. <paramref name="RunDb"/> is what <c>conductor history</c>
/// would open.</summary>
public sealed record FacePastRun(
    string Repo, string PlanName, string RunId, string Status,
    int Done, int Total, decimal CostUsd, string? LastActivityUtc, string RunDb);

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
