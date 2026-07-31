namespace Conductor.Core.Store;

// SC2.4: row types scoped to the RUN rather than to a session. Everything the store returned before
// was per-session; RUN-SUMMARY.md needs the run's own wall clock and its spend split by category,
// and neither can be derived from the session rows without getting it wrong.

/// <summary>The <c>runs</c> row itself — the only place the run's own wall clock lives. Every other
/// table times sessions, not the run, so a run that spent an hour parked between two sessions could
/// not be measured without this.</summary>
public sealed record RunRow(
    string RunId,
    string PlanName,
    string Repo,
    string? Branch,
    string? DriverVersion,
    string Status,
    string StartedUtc,
    string? EndedUtc
);

/// <summary>One <c>costs</c> category total for a run. The per-session cost figure sums EVERY
/// category (agent + gate + advisor), so anything that wants the agent/overhead split has to ask for
/// it by category or it double-counts — which is exactly what a naive summary did.</summary>
public sealed record CostCategoryRow(string Category, decimal CostUsd, long Tokens);
