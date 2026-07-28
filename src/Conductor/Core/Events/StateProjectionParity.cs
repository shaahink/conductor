using System.Globalization;
using Conductor.Models;

namespace Conductor.Core.Events;

/// <summary>
/// The B2 parity contract (D-5): the exact surface of <see cref="RunState"/> that the append-only
/// event log is the authoritative source for. <see cref="Diff"/> lists every field on that surface
/// where a folded projection disagrees with a legacy <c>state.json</c>; an empty list is parity —
/// the precondition for ever cutting over to log-as-source.
/// </summary>
/// <remarks>
/// The fields the log does <em>not</em> yet carry are deliberately OUT of the contract, so
/// <c>state.json</c> stays their cache until they are event-sourced later (additive discipline):
/// <list type="bullet">
///   <item><description>the transient control surface — <c>Status</c>, <c>AttemptsThisStage</c>,
///   <c>ConsecutiveBackoffs</c>, <c>SkippedStages</c>, <c>Pending*</c>, <c>LastGreenGateSig</c>,
///   <c>StopAfterSession</c>, <c>AttentionReason</c> (B2.3 recovery, B3 process control);</description></item>
///   <item><description>the non-event <see cref="SessionRecord"/> fields — <c>ResumeCount</c>,
///   <c>NumTurns</c>, <c>GateSummary</c>, <c>ResultSummary</c>;</description></item>
///   <item><description>timestamps — a session's <c>StartedUtc</c>/<c>EndedUtc</c> are stamped when
///   the record is created, milliseconds before the corresponding event is emitted, so they differ
///   by construction and are compared semantically (number/stage/kind/outcome) instead.</description></item>
/// </list>
/// </remarks>
public static class StateProjectionParity
{
    /// <summary>Returns one human-readable line per field where <paramref name="projected"/> differs
    /// from <paramref name="legacy"/> on the event-owned surface. Empty ⇒ parity.</summary>
    public static IReadOnlyList<string> Diff(RunState projected, RunState legacy)
    {
        ArgumentNullException.ThrowIfNull(projected);
        ArgumentNullException.ThrowIfNull(legacy);

        var diffs = new List<string>();

        Scalar(diffs, "planName", projected.PlanName, legacy.PlanName);
        Scalar(diffs, "runId", projected.RunId, legacy.RunId);
        Scalar(diffs, "currentStage", projected.CurrentStage, legacy.CurrentStage);
        Scalar(diffs, "currentStageStartHead", projected.CurrentStageStartHead, legacy.CurrentStageStartHead);
        Scalar(diffs, "sessionCounter", projected.SessionCounter, legacy.SessionCounter);
        Sequence(diffs, "confirmedStages", projected.ConfirmedStages, legacy.ConfirmedStages);
        Sequence(diffs, "auditedStages", projected.AuditedStages, legacy.AuditedStages);
        Scalar(diffs, "totalCostUsd", projected.TotalCostUsd, legacy.TotalCostUsd);
        Scalar(diffs, "totalTokensInput", projected.TotalTokensInput, legacy.TotalTokensInput);
        Scalar(diffs, "totalTokensOutput", projected.TotalTokensOutput, legacy.TotalTokensOutput);
        Scalar(diffs, "totalTokensReasoning", projected.TotalTokensReasoning, legacy.TotalTokensReasoning);

        if (projected.History.Count != legacy.History.Count)
        {
            diffs.Add($"history.count: projected={projected.History.Count} legacy={legacy.History.Count}");
            return diffs; // per-record comparison is meaningless once the counts diverge
        }

        for (var i = 0; i < legacy.History.Count; i++)
        {
            var p = projected.History[i];
            var l = legacy.History[i];
            var at = $"history[{i}]";
            Scalar(diffs, $"{at}.number", p.Number, l.Number);
            Scalar(diffs, $"{at}.stage", p.Stage, l.Stage);
            Scalar(diffs, $"{at}.kind", p.Kind, l.Kind);
            Scalar(diffs, $"{at}.attempt", p.Attempt, l.Attempt);
            Scalar(diffs, $"{at}.outcome", p.Outcome, l.Outcome);
            Scalar(diffs, $"{at}.agentSessionId", p.ClaudeSessionId, l.ClaudeSessionId);
            Sequence(diffs, $"{at}.newCommits", p.NewCommits, l.NewCommits);
            Sequence(diffs, $"{at}.newlyDone", p.NewlyDone, l.NewlyDone);
            Scalar(diffs, $"{at}.costUsd", p.CostUsd, l.CostUsd);
            Scalar(diffs, $"{at}.tokensInput", p.TokensInput, l.TokensInput);
            Scalar(diffs, $"{at}.tokensOutput", p.TokensOutput, l.TokensOutput);
            Scalar(diffs, $"{at}.tokensReasoning", p.TokensReasoning, l.TokensReasoning);
            Scalar(diffs, $"{at}.tokensCacheRead", p.TokensCacheRead, l.TokensCacheRead);
        }

        return diffs;
    }

    /// <summary>True when the folded projection matches the legacy state on the event-owned surface.</summary>
    public static bool Matches(RunState projected, RunState legacy) => Diff(projected, legacy).Count == 0;

    private static void Scalar<T>(List<string> diffs, string field, T projected, T legacy)
    {
        if (!EqualityComparer<T>.Default.Equals(projected, legacy))
            diffs.Add($"{field}: projected={Fmt(projected)} legacy={Fmt(legacy)}");
    }

    private static void Sequence(List<string> diffs, string field, IEnumerable<string> projected, IEnumerable<string> legacy)
    {
        if (!projected.SequenceEqual(legacy, StringComparer.Ordinal))
            diffs.Add($"{field}: projected=[{string.Join(",", projected)}] legacy=[{string.Join(",", legacy)}]");
    }

    private static string Fmt<T>(T value) => value switch
    {
        null => "<null>",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<null>",
    };
}
