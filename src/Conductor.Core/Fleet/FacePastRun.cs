namespace Conductor.Core.Fleet;

/// <summary>One finished run as the picker lists it. No base url and no token: there is nothing to
/// attach to and nothing to authorise. <paramref name="RunDb"/> is what <c>conductor history</c>
/// would open.</summary>
public sealed record FacePastRun(
    string Repo, string PlanName, string RunId, string Status,
    int Done, int Total, decimal CostUsd, string? LastActivityUtc, string RunDb);
