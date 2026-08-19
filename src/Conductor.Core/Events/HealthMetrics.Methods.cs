using System.Globalization;
using Conductor.Models;

namespace Conductor.Core.Events;

public static partial class HealthMetrics
{
    private enum Productivity { Productive, Unproductive, Neutral }

    public static HealthReport Compute(IEnumerable<ConductorEvent> events, Thresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var t = thresholds ?? Thresholds.Default;

        int sessions = 0, retries = 0;

        var stageStreak = new Dictionary<string, int>(StringComparer.Ordinal);
        var stageWorst = new Dictionary<string, int>(StringComparer.Ordinal);

        var gateFailStreak = new Dictionary<string, int>(StringComparer.Ordinal);
        var gateWorstFail = new Dictionary<string, int>(StringComparer.Ordinal);
        var gateLastPassed = new Dictionary<string, bool>(StringComparer.Ordinal);
        var gateFlips = new Dictionary<string, int>(StringComparer.Ordinal);

        var saturated = new List<(int Number, long Tokens)>();

        foreach (var e in events.OrderBy(e => e.Seq))
        {
            switch (e)
            {
                case SessionStarted s:
                    sessions++;
                    if (s.Attempt > 1) retries++;
                    break;

                case SessionFinished f:
                    switch (Classify(f.Outcome))
                    {
                        case Productivity.Productive:
                            stageStreak[f.StageId] = 0;
                            break;
                        case Productivity.Unproductive:
                            var n = stageStreak.GetValueOrDefault(f.StageId) + 1;
                            stageStreak[f.StageId] = n;
                            stageWorst[f.StageId] = Math.Max(stageWorst.GetValueOrDefault(f.StageId), n);
                            break;
                        case Productivity.Neutral:
                        default:
                            break;
                    }

                    var ctx = Math.Max(f.TokensCacheRead ?? 0, f.TokensInput ?? 0);
                    if (ctx >= t.ContextSaturationTokens) saturated.Add((f.Number, ctx));
                    break;

                case GateFinished { Skipped: false } g:
                    if (g.Passed)
                    {
                        gateFailStreak[g.Name] = 0;
                    }
                    else
                    {
                        var n2 = gateFailStreak.GetValueOrDefault(g.Name) + 1;
                        gateFailStreak[g.Name] = n2;
                        gateWorstFail[g.Name] = Math.Max(gateWorstFail.GetValueOrDefault(g.Name), n2);
                    }

                    if (gateLastPassed.TryGetValue(g.Name, out var prev) && prev != g.Passed)
                        gateFlips[g.Name] = gateFlips.GetValueOrDefault(g.Name) + 1;
                    gateLastPassed[g.Name] = g.Passed;
                    break;

                default:
                    break;
            }
        }

        var flags = new List<HealthFlag>();

        foreach (var (stage, streak) in stageWorst)
            if (streak >= t.FailureLoopStreak)
                flags.Add(new HealthFlag(Severity.Alert, "same-failure-loop",
                    $"stage {stage}: {streak} consecutive sessions made no progress"));

        foreach (var (gate, streak) in gateWorstFail)
            if (streak >= t.GateFailureStreak)
                flags.Add(new HealthFlag(Severity.Alert, "gate-repetition",
                    $"gate '{gate}' failed {streak}x in a row"));

        foreach (var (gate, flips) in gateFlips)
            if (flips >= t.GateOscillationFlips)
                flags.Add(new HealthFlag(Severity.Warn, "gate-oscillation",
                    $"gate '{gate}' flipped pass/fail {flips}x"));

        foreach (var (number, tokens) in saturated)
            flags.Add(new HealthFlag(Severity.Warn, "context-saturation",
                $"session #{number}: {tokens.ToString("n0", CultureInfo.InvariantCulture)} context tokens " +
                $"(≥ {t.ContextSaturationTokens.ToString("n0", CultureInfo.InvariantCulture)})"));

        var retryRate = sessions == 0 ? 0d : (double)retries / sessions;
        if (sessions >= t.MinSessionsForRetryFlag && retryRate > t.HighRetryRate)
            flags.Add(new HealthFlag(Severity.Warn, "high-retry-rate",
                $"{retries}/{sessions} sessions were retries ({retryRate.ToString("P0", CultureInfo.InvariantCulture)})"));

        flags.Sort(static (a, b) =>
        {
            var s2 = b.Severity.CompareTo(a.Severity);
            if (s2 != 0) return s2;
            var c2 = string.CompareOrdinal(a.Code, b.Code);
            return c2 != 0 ? c2 : string.CompareOrdinal(a.Detail, b.Detail);
        });

        return new HealthReport(sessions, retries, retryRate, flags);
    }

    /// <summary>Renders a report as display lines.</summary>
    /// <remarks>KS6.1/RCS1227: the eager wrapper is not ceremony. An iterator body does not run until the
    /// first MoveNext, so a null check written inside one throws at the foreach — a stack away from the
    /// caller that actually passed null, and only if anybody enumerates at all.</remarks>
    public static IEnumerable<string> Format(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return FormatCore(report);
    }

    private static IEnumerable<string> FormatCore(HealthReport report)
    {
        yield return $"sessions {report.Sessions} · retries {report.Retries} " +
                     $"({report.RetryRate.ToString("P0", CultureInfo.InvariantCulture)}) · overall {report.Worst}";
        if (report.Flags.Count == 0)
        {
            yield return "✓ no health concerns detected";
            yield break;
        }
        foreach (var f in report.Flags)
            yield return $"{Glyph(f.Severity)} [{f.Code}] {f.Detail}";
    }

    private static Productivity Classify(string outcome) =>
        Enum.TryParse<SessionOutcome>(outcome, ignoreCase: true, out var o)
            ? o switch
            {
                SessionOutcome.Advanced or SessionOutcome.Progress => Productivity.Productive,
                SessionOutcome.NoProgress or SessionOutcome.GatesRed or SessionOutcome.Stalled
                    or SessionOutcome.TimedOut or SessionOutcome.AgentError
                    or SessionOutcome.AuthFailed => Productivity.Unproductive,
                _ => Productivity.Neutral,
            }
            : Productivity.Neutral;

    private static string Glyph(Severity s) => s switch
    {
        Severity.Alert => "⛔",
        Severity.Warn => "⚠",
        _ => "·",
    };
}
