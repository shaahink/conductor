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
            // SC4.3: and it reads them across every repo the plan declares, so a pair of stalls that
            // still landed satellite commits is not "identical failure, nothing produced".
            SessionOutcome.Stalled or SessionOutcome.TimedOut =>
                !SessionProgress.HasWorkCommits(current)
                && string.IsNullOrWhiteSpace(current.ResultSummary)
                && !SessionProgress.HasWorkCommits(previous)
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
            // KS4.2/KS4.3: a gate red for its CLASS exited 0, so the exit-code test alone leaves it
            // out of the fingerprint — and two consecutive sessions failing the same way for the
            // same reason is exactly what this breaker exists to notice.
            .Where(g => (!g.Passed || g.HasClassFailure) && !g.Skipped)
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
