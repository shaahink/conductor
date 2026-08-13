using Conductor.Core.History;

namespace Conductor.Core.Money;

/// <summary>
/// KS5.1 — "what did this machine spend this week, and this month". One question, every catalogued
/// store, no repo argument and no plan argument: the operator asking it does not know which of the
/// nineteen catalogue entries the money is hiding in, and having to name one is what made the answer
/// unavailable until now.
/// <para><b>The window is applied at SESSION granularity.</b> <c>costs</c> has no timestamp column —
/// the only time anchor in the schema is <c>sessions.started_utc</c> — so a run is not in or out of a
/// week, its sessions are. The whole-run <c>--since</c> filter that <c>history</c>, <c>budget</c> and
/// <c>money</c> share answers "this week" with the whole lifetime of any run that touched it, which
/// for this repo's own long-lived runs is the entire bill.</para>
/// <para><b>Every dollar is a <c>costs.cost_usd</c> row.</b> Nothing here prices a token: the engine
/// has no price table by design (<c>LiveCostEstimator</c>), and a machine-wide figure modelled from
/// rates would be the most quotable wrong number in the project.</para>
/// <para><b>One adder.</b> Every line on this report comes out of <see cref="MoneyAnalyzer"/> —
/// <see cref="MoneyAnalyzer.Slice"/> for the windows, <see cref="MoneyAnalyzer.AnalyzeRun"/> for a
/// run's lifetime, <see cref="MoneyAnalyzer.Combine"/> for the rollup — so this verb and
/// <c>money --run &lt;id&gt;</c> cannot disagree about one cent. Pure and static, like
/// <c>BudgetAnalyzer</c>: the verb, the JSON and the tests call the same function.</para>
/// </summary>
public static class MachineLedger
{
    /// <summary>The bucket for billed rows whose session has no start time.</summary>
    public const string UndatedLabel = "undated";

    /// <summary>The label of the lifetime row.</summary>
    public const string TotalLabel = "all time";

    /// <summary>
    /// The default question, asked in three parts, in UTC — which is the clock the stored timestamps
    /// are on, and picking the operator's local midnight instead would move the boundary of "today"
    /// away from the only dates the database actually holds.
    /// <para>"This week" is the rolling seven days rather than the calendar week starting Monday: it
    /// is what <c>--since 7d</c> means everywhere else in this CLI, and the label says so rather than
    /// leaving the reader to guess which of the two they are looking at.</para>
    /// </summary>
    public static IReadOnlyList<MachineLedgerWindow> Ladder(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return
        [
            new MachineLedgerWindow("today", new DateTimeOffset(utc.Date, TimeSpan.Zero), null),
            new MachineLedgerWindow("this week (7d)", utc.AddDays(-7), null),
            new MachineLedgerWindow("this month",
                new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc)), null),
        ];
    }

    /// <summary>
    /// Measures one run out of one store: its lifetime money, and that same money sliced by each
    /// window through the session it was billed against.
    /// </summary>
    /// <param name="dbPath">The database the run was read from.</param>
    /// <param name="windows">The windows to slice by; empty is allowed and yields no period lines.</param>
    public static MachineLedgerRun Measure(
        string dbPath, string runId, string planName, string repoLabel,
        string? startedUtc, string? lastActivityUtc,
        IReadOnlyList<ArchivedSession> sessions, IReadOnlyList<ArchivedCost> costs,
        IReadOnlyList<MachineLedgerWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(windows);

        // The one time anchor. A session whose start will not parse is not "now" and not "old" — it
        // is undated, and saying so is cheaper than a guess that lands in someone's weekly figure.
        var started = new Dictionary<int, DateTimeOffset>();
        foreach (var s in sessions)
            if (RunHistory.ParseUtc(s.StartedUtc) is { } when) started[s.Number] = when;

        var periods = new List<MoneyLine>(windows.Count);
        foreach (var w in windows)
            periods.Add(MoneyAnalyzer.Slice(
                sessions.Where(s => started.TryGetValue(s.Number, out var t) && w.Contains(t)).ToList(),
                costs.Where(c => started.TryGetValue(c.SessionNumber, out var t) && w.Contains(t)).ToList(),
                w.Label));

        // Bucketed, not dropped — the same contract MoneyAnalyzer's "unknown" month keeps. These rows
        // are in the lifetime total (so the cross-check against `money` holds) and in no period (so a
        // weekly figure never quietly includes spend nobody can date).
        var undated = MoneyAnalyzer.Slice(
            sessions.Where(s => !started.ContainsKey(s.Number)).ToList(),
            costs.Where(c => !started.ContainsKey(c.SessionNumber)).ToList(),
            UndatedLabel);

        return new MachineLedgerRun(dbPath,
            MoneyAnalyzer.AnalyzeRun(runId, planName, repoLabel, startedUtc, lastActivityUtc, sessions, costs, []),
            periods, undated);
    }

    /// <summary>
    /// Rolls measured runs up into the machine's answer, counting each real run exactly once.
    /// <para><b>Identity is the run id.</b> Two catalogue entries pointing at one <c>run.db</c>, and
    /// one run copied into two stores, are both the same run twice — KS0.1 found this machine holding
    /// 37 rows for 25 runs — and a machine-wide total that adds them is wrong by however many copies
    /// the index happens to hold today.</para>
    /// </summary>
    public static MachineLedgerReport Build(
        string scope, string root, IReadOnlyList<MachineLedgerWindow> windows,
        IReadOnlyList<MachineLedgerRun> measured)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(measured);

        var kept = new Dictionary<string, MachineLedgerRun>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in measured)
        {
            if (!kept.TryGetValue(m.Run.RunId, out var already)) { kept[m.Run.RunId] = m; continue; }
            if (Fuller(m, already)) kept[m.Run.RunId] = m;
        }

        var runs = kept.Values
            .OrderBy(r => RunHistory.ParseUtc(r.Run.LastActivityUtc)
                          ?? RunHistory.ParseUtc(r.Run.StartedUtc)
                          ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.Run.RunId, StringComparer.Ordinal)
            .ToList();

        var periods = new List<MoneyLine>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var line = MoneyLine.Empty(windows[i].Label);
            foreach (var r in runs)
                if (i < r.Periods.Count)
                    line = line.Plus(r.Periods[i], windows[i].Label);
            periods.Add(line);
        }

        return new MachineLedgerReport(scope, root,
            runs.Select(r => r.DbPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            measured.Count - runs.Count,
            runs,
            MoneyAnalyzer.Combine(scope, runs.Select(r => r.Run).ToList()),
            periods,
            runs.Aggregate(MoneyLine.Empty(UndatedLabel), (acc, r) => acc.Plus(r.Undated, UndatedLabel)));
    }

    /// <summary>Which of two copies of one run to keep: the one that has recorded more of it. A copy
    /// taken mid-run and left behind is the shorter one, and keeping it would under-report a run this
    /// machine finished. Ties fall to the lower path so the answer is the same on every listing.</summary>
    private static bool Fuller(MachineLedgerRun candidate, MachineLedgerRun kept)
    {
        var a = candidate.Run.Total;
        var b = kept.Run.Total;
        if (a.Cost != b.Cost) return a.Cost > b.Cost;
        if (a.Tokens != b.Tokens) return a.Tokens > b.Tokens;
        if (a.Sessions != b.Sessions) return a.Sessions > b.Sessions;
        return string.CompareOrdinal(candidate.DbPath, kept.DbPath) < 0;
    }
}
