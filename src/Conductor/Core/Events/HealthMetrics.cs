using System.Globalization;
using Conductor.Models;

namespace Conductor.Core.Events;

/// <summary>
/// B5.3 — the AI-health projection. Folds the append-only event log into execution-health signals so
/// a human (or, later, the brain) can see when a run is <em>thrashing</em> rather than progressing:
/// how often sessions retry, whether a stage is stuck in a same-failure loop, whether the same gate
/// keeps failing (same-command repetition) or flip-flops pass/fail (oscillation), and whether a
/// session's context has blown up (F-8). This is the "is the agent healthy?" panel of "Jaeger for AI
/// agents".
/// </summary>
/// <remarks>
/// <para>Pure fold over the single event log (B5 trap: never a parallel bookkeeping store that can
/// drift) — depends only on the events, never on disk or wall-clock, so it is deterministic and
/// unit-testable. The current schema logs no <c>Thought</c>/<c>ToolCalled</c>/<c>CommandStarted</c>
/// events, so the heuristics derive from the transitions that ARE recorded: session outcomes and the
/// gate battery. The gate stream is the faithful proxy for "commands Conductor re-runs" today; once
/// per-turn command/tool events land (B9 / provider stream) the same fold sharpens to the tool level
/// without changing this contract.</para>
/// <para>Thresholds are deliberately <b>conservative</b> — a false "looping" alarm erodes trust
/// (B5 trap) — and are injectable via <see cref="Thresholds"/> so they are unit-tested against
/// synthetic loop / oscillation / saturation streams rather than hard-coded magic numbers.</para>
/// </remarks>
public static class HealthMetrics
{
    /// <summary>Health severity, worst-wins. <see cref="Ok"/> means no concern; <see cref="Warn"/> is
    /// worth a glance; <see cref="Alert"/> is a likely stuck/looping run needing a human.</summary>
    public enum Severity { Ok, Warn, Alert }

    /// <summary>Tunable, conservative trip-points for the health heuristics. Defaults are chosen so a
    /// normal fix cycle (one fail → one pass) never trips a flag; only genuine thrashing does.</summary>
    public sealed record Thresholds
    {
        /// <summary>Consecutive unproductive sessions on one stage before it counts as a same-failure loop.</summary>
        public int FailureLoopStreak { get; init; } = 3;

        /// <summary>Consecutive failures of one gate before it counts as same-command repetition.</summary>
        public int GateFailureStreak { get; init; } = 3;

        /// <summary>Pass/fail flips of one gate before it counts as oscillation (a lone fail→pass fix is 1 flip).</summary>
        public int GateOscillationFlips { get; init; } = 3;

        /// <summary>Context tokens (cache-read or input) in a single session before it counts as saturated (F-8 hit ~28.5M).</summary>
        public long ContextSaturationTokens { get; init; } = 20_000_000;

        /// <summary>Retry-rate above which the run is flagged — only once <see cref="MinSessionsForRetryFlag"/> sessions exist.</summary>
        public double HighRetryRate { get; init; } = 0.5;

        /// <summary>Minimum sessions before a retry-rate flag can fire (avoids a tiny-sample false alarm).</summary>
        public int MinSessionsForRetryFlag { get; init; } = 4;

        public static Thresholds Default { get; } = new();
    }

    /// <summary>One triggered health concern: its severity, a stable machine <paramref name="Code"/>,
    /// and a human-readable <paramref name="Detail"/>.</summary>
    public sealed record HealthFlag(Severity Severity, string Code, string Detail);

    /// <summary>The folded health of a run: headline retry stats plus every triggered flag.</summary>
    public sealed record HealthReport(int Sessions, int Retries, double RetryRate, IReadOnlyList<HealthFlag> Flags)
    {
        /// <summary>The worst severity across all flags (<see cref="Severity.Ok"/> when clean).</summary>
        public Severity Worst => Flags.Count == 0 ? Severity.Ok : Flags.Max(f => f.Severity);
    }

    /// <summary>A session outcome is <em>productive</em> (real forward motion, resets a loop streak),
    /// <em>unproductive</em> (stuck — counts toward a loop), or <em>neutral</em> (external/crash — does
    /// not count either way, so backoffs and interruptions never masquerade as a loop).</summary>
    private enum Productivity { Productive, Unproductive, Neutral }

    /// <summary>Fold the event stream into a <see cref="HealthReport"/>. Ordered by <c>Seq</c> so the
    /// consecutive-streak and oscillation folds see events in the order they happened, regardless of
    /// input ordering.</summary>
    public static HealthReport Compute(IEnumerable<ConductorEvent> events, Thresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var t = thresholds ?? Thresholds.Default;

        int sessions = 0, retries = 0;

        // Same-failure loop: current + worst consecutive unproductive-session streak per stage.
        var stageStreak = new Dictionary<string, int>(StringComparer.Ordinal);
        var stageWorst = new Dictionary<string, int>(StringComparer.Ordinal);

        // Gate stream (the re-run "commands" today): consecutive-fail streak and pass/fail flips per gate.
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
                            break; // leave the streak untouched
                    }

                    // Context tokens: cache-read is the dominant saturation signal (F-8), but fall back
                    // to input for providers that don't report a cache. Either crossing the line trips it.
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
                        var n = gateFailStreak.GetValueOrDefault(g.Name) + 1;
                        gateFailStreak[g.Name] = n;
                        gateWorstFail[g.Name] = Math.Max(gateWorstFail.GetValueOrDefault(g.Name), n);
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

        // Deterministic order (Alert first, then by code/detail) so the panel/report render identically
        // regardless of dictionary enumeration order — the same invariant the timeline/replay folds hold.
        flags.Sort(static (a, b) =>
        {
            var s = b.Severity.CompareTo(a.Severity);
            if (s != 0) return s;
            var c = string.CompareOrdinal(a.Code, b.Code);
            return c != 0 ? c : string.CompareOrdinal(a.Detail, b.Detail);
        });

        return new HealthReport(sessions, retries, retryRate, flags);
    }

    /// <summary>Render the health report as the lines shared by the REPORT.md section and the TUI
    /// health panel, so both read identically from the single fold.</summary>
    public static IEnumerable<string> Format(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
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
                    or SessionOutcome.TimedOut or SessionOutcome.AgentError => Productivity.Unproductive,
                // LimitBackoff / KilledByUser / Interrupted are external or crash-driven, not the agent looping.
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
