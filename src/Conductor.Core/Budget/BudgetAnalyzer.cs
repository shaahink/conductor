using System.Globalization;
using Conductor.Core.History;

namespace Conductor.Core.Budget;

/// <summary>
/// K4.2 — measures a run's token budget out of what the run recorded, and prescribes the next one.
/// <para>The method is <c>docs/dev/TOKEN-BUDGET-TUNING.md</c> §7, and every input it needs is already
/// in the archive, so nothing here takes a number from a caller: the ceiling comes from the
/// <c>tokenBudget</c> the rail stamped on its own <c>SoftBreakRequested</c> events (or, for a run that
/// never nudged, from where its kills cluster), the nudge point from the <c>liveTokens</c> on the same
/// event, the floor from the smallest session that closed a checkpoint, and the wrap-up from what
/// clean enders spent AFTER being nudged. A run analysed this way states its own configuration even
/// when its database predates <c>runs.limits</c> — which every run in this repo does.</para>
/// <para>Pure and static on purpose: the verb, <c>doctor</c> and the tests all call the same function
/// over the same records, so a number printed on one surface cannot drift from another.</para>
/// </summary>
public static class BudgetAnalyzer
{
    /// <summary>A ceiling kill lands at or just past the ceiling, never below it. This is how much
    /// past it a final total may sit and still be recognised as the same ceiling — one turn of
    /// overshoot. Measured on this repo's face run: 8.011M..8.130M against an 8.000M ceiling.</summary>
    private const double KillBand = 1.08;

    /// <summary>§7 step 2: "or assume ~1–3M and correct later". Used only when nothing ever nudged,
    /// and always reported as an assumption rather than a measurement.</summary>
    private const long AssumedWrapUp = 1_500_000;

    /// <summary>Measures one run. <paramref name="softBreaks"/> may be empty; the analysis degrades to
    /// inference and says so, rather than refusing to answer.</summary>
    public static BudgetProfile Analyze(
        string runId, string planName,
        IReadOnlyList<ArchivedSession> sessions,
        IReadOnlyList<SoftBreakObservation> softBreaks)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(softBreaks);

        var ordered = sessions.OrderBy(s => s.Number).ToList();
        // First nudge per session: the rail can fire more than once (this repo's face run has 30
        // events over 20 nudged sessions), and the wrap-up is what was spent after the FIRST one.
        var firstNudge = new Dictionary<int, SoftBreakObservation>();
        foreach (var b in softBreaks.OrderBy(b => b.Session))
            if (!firstNudge.ContainsKey(b.Session)) firstNudge[b.Session] = b;

        var windows = SplitWindows(ordered, firstNudge);
        return new BudgetProfile(runId, planName, windows, Prescribe(windows[^1]));
    }

    // ---------------------------------------------------------------- where the ceiling changed

    private static List<BudgetWindow> SplitWindows(
        List<ArchivedSession> ordered, Dictionary<int, SoftBreakObservation> firstNudge)
    {
        var boundaries = CeilingBoundaries(ordered, firstNudge);
        var windows = new List<BudgetWindow>();
        for (var i = 0; i < boundaries.Count; i++)
        {
            var from = boundaries[i].From;
            var to = i + 1 < boundaries.Count ? boundaries[i + 1].From - 1 : int.MaxValue;
            var slice = ordered.Where(s => s.Number >= from && s.Number <= to).ToList();
            if (slice.Count > 0) windows.Add(Measure(slice, boundaries[i].Ceiling, boundaries[i].Measured, firstNudge));
        }
        return windows.Count > 0
            ? windows
            : [Measure(ordered, null, false, firstNudge)];
    }

    /// <summary>The session numbers at which the ceiling in force changed, each with the ceiling that
    /// took effect there. A run whose cap never moved yields one boundary at its first session.</summary>
    private static List<(int From, long? Ceiling, bool Measured)> CeilingBoundaries(
        List<ArchivedSession> ordered, Dictionary<int, SoftBreakObservation> firstNudge)
    {
        var result = new List<(int From, long? Ceiling, bool Measured)>();
        if (ordered.Count == 0) return result;

        // Recorded evidence first: a nudge event names the ceiling it was a fraction of.
        var stamped = firstNudge.Values
            .Where(b => b.TokenBudget is > 0)
            .OrderBy(b => b.Session)
            .Select(b => (b.Session, Ceiling: b.TokenBudget!.Value))
            .ToList();

        var measured = stamped.Count > 0;
        if (!measured)
        {
            // No run ever nudged. Fall back to where the kills cluster: `OverSessionTokenBudget` fires
            // at >= cap, so a tight cluster of rollover totals IS the cap, to within one turn.
            var inferred = InferCeilingFromKills(ordered);
            if (inferred is null) return [(ordered[0].Number, null, false)];
            stamped = [(inferred.Value.FirstSession, inferred.Value.Ceiling)];
        }

        long? previous = null;
        foreach (var (session, ceiling) in stamped)
        {
            if (previous is { } p && SameCeiling(p, ceiling)) continue;
            // Pull the boundary back over any kill that already sat on this ceiling but preceded the
            // first nudge — in this repo's face run the ceiling's first victim (session 9) rolled over
            // before any session lived long enough to be nudged (session 10).
            var earliest = ordered
                .Where(s => s.Number < session && IsRollover(s) && InKillBand(s.CapTokens, ceiling))
                .Select(s => s.Number)
                .DefaultIfEmpty(session)
                .Min();
            result.Add((earliest, ceiling, measured));
            previous = ceiling;
        }

        // Sessions before the first ceiling only form their own window if they PROVE there was none:
        // one of them must have run past it. Otherwise they were simply never tested.
        var firstCeiling = result.Count > 0 ? result[0] : (From: ordered[0].Number, Ceiling: (long?)null, Measured: false);
        if (result.Count > 0 && firstCeiling.From > ordered[0].Number)
        {
            var before = ordered.Where(s => s.Number < firstCeiling.From).ToList();
            if (before.Any(s => s.CapTokens > firstCeiling.Ceiling!.Value * KillBand))
                result.Insert(0, (ordered[0].Number, null, false));
            else
                result[0] = (ordered[0].Number, firstCeiling.Ceiling, firstCeiling.Measured);
        }
        if (result.Count == 0) result.Add((ordered[0].Number, null, false));
        return result;
    }

    /// <summary>The ceiling a run never named, read off the shape of its kills: the largest set of
    /// rollover totals that all sit within one turn's overshoot of each other. Fewer than three is
    /// coincidence, not a ceiling.</summary>
    private static (long Ceiling, int FirstSession)? InferCeilingFromKills(List<ArchivedSession> ordered)
    {
        var kills = ordered.Where(s => IsRollover(s) && s.CapTokens > 0).OrderBy(s => s.CapTokens).ToList();
        if (kills.Count < 3) return null;

        var bestStart = 0;
        var bestCount = 0;
        for (var i = 0; i < kills.Count; i++)
        {
            var j = i;
            while (j + 1 < kills.Count && kills[j + 1].CapTokens <= kills[i].CapTokens * KillBand) j++;
            if (j - i + 1 > bestCount) { bestCount = j - i + 1; bestStart = i; }
        }
        if (bestCount < 3) return null;

        var cluster = kills.GetRange(bestStart, bestCount);
        // The kill fires at >= cap, so the smallest total in the cluster is the tightest upper bound
        // on the cap. Round it down to the granularity a human would have typed.
        var ceiling = RoundDownToConfigGrain(cluster.Min(s => s.CapTokens));
        return (ceiling, cluster.Min(s => s.Number));
    }

    // ---------------------------------------------------------------- one window's numbers

    private static BudgetWindow Measure(
        List<ArchivedSession> slice, long? ceiling, bool ceilingMeasured,
        Dictionary<int, SoftBreakObservation> firstNudge)
    {
        var costed = slice.Where(s => s.CapTokens > 0).ToList();
        var closers = costed.Where(s => s.ClosedCheckpoints.Count > 0).Select(s => s.CapTokens).OrderBy(t => t).ToList();
        var nudged = costed.Where(s => firstNudge.ContainsKey(s.Number)).ToList();
        var clean = nudged.Where(s => !IsRollover(s) && !IsKilledByUser(s)).ToList();

        var wrapUps = clean
            .Select(s => s.CapTokens - firstNudge[s.Number].LiveTokens)
            .Where(v => v > 0)
            .OrderBy(v => v)
            .ToList();

        // The nudge point is measured, not derived from a ratio: the median of where the rail actually
        // fired. It is not exactly ratio x cap because the rail rides a tool call and lands on the
        // first turn past the threshold.
        long? nudgePoint = nudged.Count > 0
            ? Median(nudged.Select(s => firstNudge[s.Number].LiveTokens).OrderBy(v => v).ToList())
            : null;

        return new BudgetWindow(
            Label: Label(ceiling, ceilingMeasured, nudgePoint),
            FirstSession: slice[0].Number,
            LastSession: slice[^1].Number,
            Sessions: slice.Count,
            Costed: costed.Count,
            CapTokens: ceiling,
            CapMeasured: ceilingMeasured,
            NudgeTokens: nudgePoint,
            Tokens: costed.Sum(s => s.CapTokens),
            Checkpoints: costed.Sum(s => s.ClosedCheckpoints.Count),
            Rollovers: costed.Count(IsRollover),
            Nudged: nudged.Count,
            NudgedAndClean: clean.Count,
            RolloversNudgedFirst: costed.Count(s => IsRollover(s) && firstNudge.ContainsKey(s.Number)),
            Closers: closers.Count,
            Floor: closers.Count > 0 ? closers[0] : 0,
            ClosingMedian: closers.Count > 0 ? Median(closers) : 0,
            ClosingMax: closers.Count > 0 ? closers[^1] : 0,
            WrapUp: wrapUps.Count > 0 ? (wrapUps[0], Median(wrapUps), wrapUps[^1], wrapUps.Count) : null);
    }

    private static string Label(long? ceiling, bool measured, long? nudge)
    {
        if (ceiling is not { } c) return "no ceiling observed";
        var text = Millions(c) + (measured ? "" : " (inferred)");
        return nudge is { } n ? $"{text} / nudge {Millions(n)}" : $"{text} / never nudged";
    }

    // ---------------------------------------------------------------- the prescription

    /// <summary>§7 applied to a measured window. The rule the research pass added to §7 is the one
    /// that matters here: the nudge must clear the LARGEST session that ever closed a checkpoint, not
    /// merely the floor — a nudge under it fires on every session before it could have finished.</summary>
    private static BudgetPrescription Prescribe(BudgetWindow w)
    {
        var findings = new List<string>();
        var wrapUp = w.WrapUp?.Median ?? AssumedWrapUp;
        var measured = w.WrapUp is not null;

        if (w.Closers == 0)
        {
            findings.Add("no session in this window closed a checkpoint, so there is no floor to measure and no cap can be prescribed honestly.");
            return new BudgetPrescription(w.CapTokens ?? 0, w.NudgeRatio ?? 0, w.NudgeTokens ?? 0,
                w.Headroom ?? 0, wrapUp, measured, findings, "not enough delivered work to prescribe a budget - run uncapped until something closes.");
        }

        // The nudge has to clear the biggest closer with a margin, and it has to clear the floor plus
        // one wrap-up, or a floor-sized session gets nudged before it has done anything.
        var nudge = Math.Max((long)(w.ClosingMax * 1.05), w.Floor + wrapUp);
        var cap = RoundUpToConfigGrain(nudge + 2 * wrapUp);
        var ratio = Math.Round(nudge / (double)cap / 0.05, MidpointRounding.AwayFromZero) * 0.05;
        while (ratio < 0.9 && ratio * cap <= w.ClosingMax) ratio += 0.05;
        while (ratio > 0.5 && cap - ratio * cap < 1.5 * wrapUp) ratio -= 0.05;
        ratio = Math.Round(ratio, 2);
        var prescribedNudge = (long)(cap * ratio);
        var headroom = cap - prescribedNudge;

        if (w.CapTokens is { } current)
        {
            if (current < w.Floor)
                findings.Add($"CAP BELOW FLOOR: {Millions(current)} is under the {Millions(w.Floor)} floor - nothing can land in one session and the total rises. This is the failure that made one run worse than uncapped.");
            if (w.NudgeTokens is { } n)
            {
                var x = n / (double)w.ClosingMedian;
                if (x < 1.0)
                    findings.Add($"NUDGE BELOW THE MEDIAN CLOSER: it fires at {Millions(n)}, {x.ToString("0.00", CultureInfo.InvariantCulture)}x the {Millions(w.ClosingMedian)} median closing session - every session is interrupted before it could have finished naturally.");
                if (w.Headroom is { } h && h < 1.5 * wrapUp)
                    findings.Add($"HEADROOM THIN: {Millions(h)} after the nudge is {(h / (double)wrapUp).ToString("0.0", CultureInfo.InvariantCulture)}x the {(measured ? "measured" : "assumed")} {Millions(wrapUp)} wrap-up; the rule is >= 1.5x.");
            }
            else if (w.Rollovers > 0)
            {
                findings.Add("the ceiling killed sessions but the cooperative rail never fired - check that the soft-break hook is delivered at all.");
            }
            if (w.Rollovers > 0 && w.RolloversNudgedFirst == w.Rollovers)
                findings.Add($"THE RAIL IS DELIVERED AND IGNORED: all {w.Rollovers} killed sessions had already been nudged and not one of them stopped. The cooperative break is the only path that ends a capped session on its own terms, and here it converted zero.");
            else if (w.Rollovers > 0 && w.RolloversNudgedFirst > 0)
                findings.Add($"{w.RolloversNudgedFirst} of {w.Rollovers} killed sessions had been nudged first and kept going.");
            if (w.RolloverRate > 0.25)
                findings.Add($"ROLLOVER RATE {(w.RolloverRate * 100).ToString("0", CultureInfo.InvariantCulture)}% ({w.Rollovers} of {w.Costed}) - above a quarter, the cap is buying churn as much as discipline.");
        }
        else
        {
            findings.Add($"no ceiling was in force over these {w.Costed} sessions.");
        }
        if (!measured)
            findings.Add($"wrap-up is ASSUMED at {Millions(AssumedWrapUp)} (TOKEN-BUDGET-TUNING.md section 7 step 2) - no session in this window was ever nudged, so there is nothing to measure. Re-run this after one is.");

        var verdict = w.CapTokens is { } cur && SameCeiling(cur, cap)
            ? $"your budget is already where the measurements put it - {Millions(cap)} at {ratio.ToString("0.##", CultureInfo.InvariantCulture)}, nudge {Millions(prescribedNudge)}."
            : $"set maxSessionTokens to {Millions(cap)} at softBreakRatio {ratio.ToString("0.##", CultureInfo.InvariantCulture)} - nudge {Millions(prescribedNudge)} clears the {Millions(w.ClosingMax)} largest closer, headroom {Millions(headroom)} is {(headroom / (double)wrapUp).ToString("0.0", CultureInfo.InvariantCulture)}x the {(measured ? "measured" : "assumed")} {Millions(wrapUp)} wrap-up.";

        return new BudgetPrescription(cap, ratio, prescribedNudge, headroom, wrapUp, measured, findings, verdict);
    }

    // ---------------------------------------------------------------- small shared arithmetic

    /// <summary>Formats tokens the way every other surface in this tree does.</summary>
    public static string Millions(long tokens) =>
        (tokens / 1_000_000.0).ToString(tokens >= 10_000_000 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "M";

    private static bool IsRollover(ArchivedSession s) =>
        string.Equals(s.Outcome, "RolledOver", StringComparison.OrdinalIgnoreCase);

    private static bool IsKilledByUser(ArchivedSession s) =>
        string.Equals(s.Outcome, "KilledByUser", StringComparison.OrdinalIgnoreCase);

    private static bool InKillBand(long tokens, long ceiling) =>
        tokens >= ceiling * 0.98 && tokens <= ceiling * KillBand;

    private static bool SameCeiling(long a, long b) => Math.Abs(a - b) <= Math.Max(a, b) * 0.02;

    private static long Median(List<long> sorted) => sorted.Count == 0
        ? 0
        : sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>Caps are typed by people: whole millions once they are big, half-millions below ten.</summary>
    private static long RoundUpToConfigGrain(long tokens)
    {
        var grain = tokens >= 10_000_000 ? 1_000_000 : 500_000;
        return (tokens + grain - 1) / grain * grain;
    }

    private static long RoundDownToConfigGrain(long tokens)
    {
        var grain = tokens >= 10_000_000 ? 1_000_000 : 250_000;
        return tokens / grain * grain;
    }
}
