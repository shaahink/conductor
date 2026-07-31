using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// F3.3: Same-failure circuit breaker. When 2 consecutive sessions end with the same non-success
/// outcome and matching symptoms, route to Advisor instead of queuing another fix or retry. This
/// prevents the attempt budget from burning on identical failures.
/// </summary>
public static class FailureCircuitBreaker
{
    private static readonly HashSet<SessionOutcome> BreakableOutcomes = new()
    {
        SessionOutcome.Stalled,
        SessionOutcome.TimedOut,
        SessionOutcome.GatesRed,
        SessionOutcome.AgentError,
        SessionOutcome.NoProgress,
    };

    public static bool ShouldBreak(
        SessionRecord? previous,
        SessionRecord current,
        IReadOnlyList<GateResult>? currentGates)
    {
        if (previous == null) return false;
        if (current.Outcome == null || previous.Outcome != current.Outcome) return false;
        if (!BreakableOutcomes.Contains(current.Outcome.Value)) return false;

        return current.Outcome switch
        {
            // SC4.2: "produced nothing" reads the AGENT's commits — conductor's own
            // chore(conductor): status writes are not output, and letting one of them stand in for
            // work would keep the breaker open through an otherwise identical pair of stalls.
            SessionOutcome.Stalled or SessionOutcome.TimedOut =>
                Git.ExcludeBookkeeping(current.NewCommits).Count == 0
                && string.IsNullOrWhiteSpace(current.ResultSummary)
                && Git.ExcludeBookkeeping(previous.NewCommits).Count == 0
                && string.IsNullOrWhiteSpace(previous.ResultSummary),

            SessionOutcome.GatesRed or SessionOutcome.NoProgress =>
                FailingGates(currentGates).SetEquals(ParseFailingGates(previous.GateSummary)),

            SessionOutcome.AgentError => true,

            _ => false,
        };
    }

    private static HashSet<string> FailingGates(IReadOnlyList<GateResult>? gates)
    {
        if (gates == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return gates
            .Where(g => !g.Passed && !g.Skipped)
            .Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static HashSet<string> ParseFailingGates(string gateSummary)
    {
        var failing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(gateSummary)) return failing;
        var parts = gateSummary.Split(" · ");
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':');
            if (colon < 0) continue;
            var name = part[..colon].Trim();
            var glyph = part[(colon + 1)..].Trim();
            if (glyph is "✗" or "✘" or "x" or "X")
                failing.Add(name);
        }
        return failing;
    }
}
