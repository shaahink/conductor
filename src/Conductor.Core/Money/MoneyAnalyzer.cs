using Conductor.Core.Budget;
using Conductor.Core.History;

namespace Conductor.Core.Money;

/// <summary>
/// K4.3 — turns a run's recorded <c>costs</c> rows into the report the owner keeps asking for. Every
/// figure in the research doc's headline table (<c>docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md</c>) was
/// produced by a hand-written query against a database the operator had to find first; this is that
/// query, once, in a place three surfaces read.
/// <para><b>The one rule.</b> Money comes from what the provider billed, never from a price table —
/// the engine deliberately has none (<c>LiveCostEstimator</c>). So a blended rate here is billed
/// dollars over billed tokens, and a cache-heavy row reads cheap per token because it WAS cheap.</para>
/// <para><b>The window axis is borrowed, not re-derived.</b> "What did the cap buy me" is a comparison
/// between the stretches either side of a ceiling change, and <see cref="BudgetAnalyzer"/> already
/// measures where those changes happened. Re-deriving the split here would give the two verbs two
/// answers to the same question.</para>
/// <para>Pure and static, like <see cref="BudgetAnalyzer"/>, for the same reason: the verb, the report
/// and the tests all call the same function over the same records.</para>
/// </summary>
public static class MoneyAnalyzer
{
    /// <summary>Measures one run. <paramref name="windows"/> may be empty — a run whose ceiling never
    /// moved has nothing to compare, and the report says so by printing no window section rather than
    /// by inventing a split.</summary>
    public static MoneyRun AnalyzeRun(
        string runId, string planName, string repoLabel, string? startedUtc, string? lastActivityUtc,
        IReadOnlyList<ArchivedSession> sessions, IReadOnlyList<ArchivedCost> costs,
        IReadOnlyList<BudgetWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(windows);

        var byNumber = new Dictionary<int, ArchivedSession>();
        foreach (var s in sessions) byNumber[s.Number] = s;

        var total = Fill(new Accumulator(), sessions, costs).ToLine("run total");

        var windowLines = new List<MoneyLine>();
        foreach (var w in windows)
        {
            var slice = sessions.Where(s => s.Number >= w.FirstSession && s.Number <= w.LastSession).ToList();
            var rows = costs.Where(c => c.SessionNumber >= w.FirstSession && c.SessionNumber <= w.LastSession).ToList();
            if (slice.Count == 0 && rows.Count == 0) continue;
            windowLines.Add(Fill(new Accumulator(), slice, rows)
                .ToLine($"{w.FirstSession}-{w.LastSession} {w.Label}"));
        }

        return new MoneyRun(runId, planName, repoLabel, startedUtc, lastActivityUtc, total,
            windowLines,
            Grouped(sessions, costs, byNumber, s => Stage(s), byFirstSession: true),
            Grouped(sessions, costs, byNumber, s => Month(s?.StartedUtc) ?? "unknown", byFirstSession: false),
            Categories(costs));
    }

    /// <summary>Rolls runs up into one report. The month rows are merged across runs on purpose: a
    /// project that ran two plans in July spent one July.</summary>
    public static MoneyReport Combine(string scope, IReadOnlyList<MoneyRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var total = runs.Aggregate(MoneyLine.Empty("project total"), (acc, r) => acc.Plus(r.Total, "project total"));
        return new MoneyReport(scope, runs, total,
            Merge(runs.SelectMany(r => r.Months)).OrderBy(l => l.Label, StringComparer.Ordinal).ToList(),
            Merge(runs.SelectMany(r => r.Categories)).OrderByDescending(l => l.Cost).ToList());
    }

    /// <summary>The calendar month a recorded timestamp falls in, or null when the string is not one.
    /// Timestamps stay the strings the schema stores (see <see cref="RunArchive"/>), so this reads the
    /// prefix rather than parsing — a row from an older engine that spells its date differently must
    /// bucket as "unknown", not take the report down.</summary>
    public static string? Month(string? timestamp)
    {
        if (timestamp is null || timestamp.Length < 7) return null;
        if (timestamp[4] != '-') return null;
        for (var i = 0; i < 7; i++)
            if (i != 4 && !char.IsAsciiDigit(timestamp[i])) return null;
        return timestamp[..7];
    }

    // ---------------------------------------------------------------- bucketing

    private static List<MoneyLine> Grouped(
        IReadOnlyList<ArchivedSession> sessions, IReadOnlyList<ArchivedCost> costs,
        Dictionary<int, ArchivedSession> byNumber, Func<ArchivedSession?, string> keyOf, bool byFirstSession)
    {
        var acc = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        // Money first: a cost row whose session is missing from the table still happened, and buckets
        // under whatever key the null session yields rather than vanishing from the total.
        foreach (var c in costs)
        {
            byNumber.TryGetValue(c.SessionNumber, out var s);
            var bucket = Bucket(acc, keyOf(s));
            bucket.Add(c);
            bucket.Sessions.Add(c.SessionNumber);
            bucket.First = Math.Min(bucket.First, c.SessionNumber);
        }

        // Then delivery, from the sessions themselves: checkpoints are a property of a session, not of
        // a cost row, and counting them per row would multiply them by the number of lanes that billed.
        foreach (var s in sessions)
        {
            var bucket = Bucket(acc, keyOf(s));
            bucket.Checkpoints += s.ClosedCheckpoints.Count;
            bucket.Sessions.Add(s.Number);
            bucket.First = Math.Min(bucket.First, s.Number);
        }

        var lines = acc.Select(kv => (kv.Key, kv.Value));
        return (byFirstSession
                ? lines.OrderBy(p => p.Value.First)
                : lines.OrderBy(p => p.Key, StringComparer.Ordinal))
            .Select(p => p.Value.ToLine(p.Key))
            .ToList();
    }

    /// <summary>The spending lanes. No checkpoint column: a gate battery closes nothing, and printing
    /// "$0.00 per checkpoint" against it would be arithmetic dressed as a finding.</summary>
    private static List<MoneyLine> Categories(IReadOnlyList<ArchivedCost> costs)
    {
        var acc = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in costs)
        {
            var bucket = Bucket(acc, string.IsNullOrWhiteSpace(c.Category) ? "unknown" : c.Category);
            bucket.Add(c);
            bucket.Sessions.Add(c.SessionNumber);
        }
        return acc.Select(kv => kv.Value.ToLine(kv.Key)).OrderByDescending(l => l.Cost).ToList();
    }

    /// <summary>Adds rows that share a label. Session counts add rather than union — across runs the
    /// same session number means two different sessions.</summary>
    private static List<MoneyLine> Merge(IEnumerable<MoneyLine> lines)
    {
        var byLabel = new Dictionary<string, MoneyLine>(StringComparer.Ordinal);
        foreach (var l in lines)
            byLabel[l.Label] = byLabel.TryGetValue(l.Label, out var existing) ? existing.Plus(l, l.Label) : l;
        return byLabel.Values.ToList();
    }

    private static Accumulator Bucket(Dictionary<string, Accumulator> acc, string key)
    {
        if (acc.TryGetValue(key, out var existing)) return existing;
        return acc[key] = new Accumulator();
    }

    private static Accumulator Fill(
        Accumulator acc, IEnumerable<ArchivedSession> sessions, IEnumerable<ArchivedCost> costs)
    {
        foreach (var c in costs) { acc.Add(c); acc.Sessions.Add(c.SessionNumber); }
        foreach (var s in sessions) { acc.Checkpoints += s.ClosedCheckpoints.Count; acc.Sessions.Add(s.Number); }
        return acc;
    }

    private static string Stage(ArchivedSession? s) =>
        string.IsNullOrWhiteSpace(s?.StageId) ? "(no stage)" : s.StageId;

    private sealed class Accumulator
    {
        public readonly HashSet<int> Sessions = [];
        public long Tokens;
        public long CacheRead;
        public long Input;
        public long Output;
        public decimal Cost;
        public int Checkpoints;
        public int First = int.MaxValue;

        public void Add(ArchivedCost c)
        {
            Tokens += c.Tokens;
            CacheRead += c.TokensCacheRead;
            Input += c.TokensIn;
            Output += c.TokensOut;
            Cost += c.CostUsd;
        }

        public MoneyLine ToLine(string label) =>
            new(label, Sessions.Count, Tokens, CacheRead, Input, Output, Cost, Checkpoints);
    }
}
