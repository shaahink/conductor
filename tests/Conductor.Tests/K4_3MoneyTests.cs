using System.Globalization;
using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Money;
using Conductor.Models;
using Microsoft.Data.Sqlite;

namespace Conductor.Tests;

/// <summary>
/// K4.3 — <c>conductor money</c>. The measurements this file pins are the ones the research doc
/// produced by hand: the headline row (sessions, tokens, cache-read share, cost, checkpoints,
/// tokens and dollars per checkpoint), the split by stage and by month, and the before/after windows
/// that answer "what did the cap buy me".
/// <para>The seeded run is small enough to check with arithmetic in your head, which is the point: a
/// report whose totals cannot be verified by hand is a report nobody can trust.</para>
/// </summary>
public sealed class K4_3MoneyTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("k43").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // Four sessions: two in stage K1 (July), two in stage K2 (August). Session 3 closed nothing.
    private static IReadOnlyList<ArchivedSession> Sessions() =>
    [
        Session(1, "K1", "2026-07-10T08:00:00Z", "K1.1"),
        Session(2, "K1", "2026-07-20T08:00:00Z", "K1.2,K1.3"),
        Session(3, "K2", "2026-08-01T08:00:00Z", null),
        Session(4, "K2", "2026-08-02T08:00:00Z", "K2.1"),
    ];

    private static ArchivedSession Session(int number, string stage, string started, string? done) =>
        new(number, stage, "Deliver", started, null, "Completed", 1, 0, 1, 0m, 0,
            null, null, NewlyDone: done);

    // 10M tokens per session, 9M of them cache reads, $8 per session on the agent lane; plus a gate
    // row on every session at $0.10 and no tokens, which is exactly how the engine records them.
    private static IReadOnlyList<ArchivedCost> Costs() =>
    [
        new(1, "agent", 900_000, 100_000, 0, 9_000_000, 8m, 0),
        new(1, "gate", 0, 0, 0, 0, 0.10m, 0),
        new(2, "agent", 900_000, 100_000, 0, 9_000_000, 8m, 0),
        new(2, "gate", 0, 0, 0, 0, 0.10m, 0),
        new(3, "agent", 900_000, 100_000, 0, 9_000_000, 8m, 0),
        new(3, "gate", 0, 0, 0, 0, 0.10m, 0),
        new(4, "agent", 900_000, 100_000, 0, 9_000_000, 8m, 0),
        new(4, "gate", 0, 0, 0, 0, 0.10m, 0),
    ];

    private static MoneyRun Run(IReadOnlyList<BudgetWindow>? windows = null) =>
        MoneyAnalyzer.AnalyzeRun("R1", "karvan", "repo", "2026-07-10T08:00:00Z", "2026-08-02T09:00:00Z",
            Sessions(), Costs(), windows ?? []);

    private static BudgetWindow Window(string label, int first, int last) => new(
        Label: label, FirstSession: first, LastSession: last, Sessions: last - first + 1,
        Costed: last - first + 1, CapTokens: null, CapMeasured: false, NudgeTokens: null,
        Tokens: 0, Checkpoints: 0, Rollovers: 0, Nudged: 0, NudgedAndClean: 0,
        RolloversNudgedFirst: 0, Closers: 0, Floor: 0, ClosingMedian: 0, ClosingMax: 0, WrapUp: null);

    // ------------------------------------------------------------------ the headline row

    [Fact]
    public void TheHeadlineRowIsTheColumnsTheResearchDocPrintedByHand()
    {
        var total = Run().Total;

        Assert.Equal(4, total.Sessions);
        Assert.Equal(40_000_000, total.Tokens);              // 4 x 10M
        Assert.Equal(36_000_000, total.CacheReadTokens);     // 4 x 9M
        Assert.Equal(0.9, total.CacheReadShare, 6);
        Assert.Equal(32.40m, total.Cost);                    // 4 x ($8 agent + $0.10 gate)
        Assert.Equal(4, total.Checkpoints);                  // K1.1, K1.2, K1.3, K2.1
        Assert.Equal(10_000_000, total.TokensPerCheckpoint!.Value, 0);
        Assert.Equal(8.10m, total.CostPerCheckpoint);
        Assert.Equal(0.81m, total.CostPerMillionTokens);     // $32.40 over 40M
    }

    [Fact]
    public void CheckpointsAreCountedOncePerSessionNotOncePerCostRow()
    {
        // Session 2 closed two checkpoints and billed on two lanes. Counting delivery off the cost
        // rows would report four for that session and halve every dollar-per-checkpoint figure.
        var stages = Run().Stages;

        Assert.Equal(3, stages.Single(s => s.Label == "K1").Checkpoints);
        Assert.Equal(1, stages.Single(s => s.Label == "K2").Checkpoints);
    }

    [Fact]
    public void TheStageAndMonthCutsSplitExactlyTheSameMoney()
    {
        var run = Run();

        Assert.Equal(run.Total.Cost, run.Stages.Sum(s => s.Cost));
        Assert.Equal(run.Total.Tokens, run.Stages.Sum(s => s.Tokens));
        Assert.Equal(run.Total.Cost, run.Months.Sum(m => m.Cost));
        Assert.Equal(run.Total.Checkpoints, run.Months.Sum(m => m.Checkpoints));
        Assert.Equal(run.Total.Cost, run.Categories.Sum(c => c.Cost));
    }

    [Fact]
    public void MonthsAreTheCalendarMonthsOfTheSessionsOldestFirst()
    {
        var months = Run().Months;

        Assert.Equal(["2026-07", "2026-08"], months.Select(m => m.Label));
        Assert.Equal(3, months[0].Checkpoints);          // sessions 1 and 2
        Assert.Equal(16.20m, months[0].Cost);
        Assert.Equal(1, months[1].Checkpoints);          // session 4; session 3 closed nothing
    }

    [Fact]
    public void ALaneThatClosesNothingReportsNoDollarsPerCheckpointRatherThanZero()
    {
        var gate = Run().Categories.Single(c => c.Label == "gate");

        Assert.Equal(0.40m, gate.Cost);
        Assert.Equal(0, gate.Checkpoints);
        Assert.Null(gate.CostPerCheckpoint);
        Assert.Null(gate.TokensPerCheckpoint);
    }

    [Fact]
    public void ACostRowWhoseSessionIsMissingStillCounts()
    {
        // A row recorded against a session the sessions table never got (a torn write, an import from
        // an older schema). It must not vanish from the total, or the report undercounts the bill.
        var costs = Costs().Append(new ArchivedCost(99, "advisor", 1_000, 0, 0, 0, 1.25m, 0)).ToList();
        var run = MoneyAnalyzer.AnalyzeRun("R1", "karvan", "repo", null, null, Sessions(), costs, []);

        Assert.Equal(33.65m, run.Total.Cost);
        Assert.Equal(1.25m, run.Stages.Single(s => s.Label == "(no stage)").Cost);
        Assert.Equal(1.25m, run.Months.Single(m => m.Label == "unknown").Cost);
    }

    [Theory]
    [InlineData("2026-08-02T09:00:00Z", "2026-08")]
    [InlineData("2026-08-02 09:00:00", "2026-08")]
    [InlineData("last tuesday", null)]
    [InlineData("2026", null)]
    [InlineData(null, null)]
    public void MonthReadsThePrefixAndRefusesAnythingElse(string? timestamp, string? expected)
        => Assert.Equal(expected, MoneyAnalyzer.Month(timestamp));

    // ------------------------------------------------------------------ what the cap bought

    [Fact]
    public void TheWindowsAreTheBudgetSplitPricedAndTheyAnswerWhatTheCapBought()
    {
        var run = Run([Window("no ceiling observed", 1, 2), Window("8M / nudge 6M", 3, 4)]);

        Assert.Equal(2, run.Windows.Count);
        Assert.Equal("1-2 no ceiling observed", run.Windows[0].Label);
        Assert.Equal(16.20m, run.Windows[0].Cost);
        Assert.Equal(3, run.Windows[0].Checkpoints);
        Assert.Equal(1, run.Windows[1].Checkpoints);
        // $5.40/ckpt before, $16.20/ckpt after: the later window costs three times as much per
        // delivered checkpoint, so the payoff is a third and must read as WORSE, not as 3x better.
        Assert.Equal(0.333m, run.CapCostPayoff!.Value, 3);
        Assert.Equal(0.333, run.CapTokenPayoff!.Value, 3);
    }

    [Fact]
    public void WindowsBuiltFromARealBudgetSplitLineUpWithIt()
    {
        // The axis is borrowed, not re-derived: the money windows must carry the ceiling labels the
        // budget analyzer measured, over the same session ranges.
        var sessions = Sessions();
        var profile = BudgetAnalyzer.Analyze("R1", "karvan", sessions, []);
        var run = MoneyAnalyzer.AnalyzeRun("R1", "karvan", "repo", null, null, sessions, Costs(), profile.Windows);

        Assert.Equal(profile.Windows.Count, run.Windows.Count);
        Assert.EndsWith(profile.Windows[0].Label, run.Windows[0].Label, StringComparison.Ordinal);
        Assert.Equal(run.Total.Cost, run.Windows.Sum(w => w.Cost));
    }

    [Fact]
    public void CombineRollsRunsUpIntoAProjectAndMergesTheirMonths()
    {
        var report = MoneyAnalyzer.Combine("this repo", [Run(), Run()]);

        Assert.Equal(64.80m, report.Total.Cost);
        Assert.Equal(8, report.Total.Checkpoints);
        Assert.Equal(8, report.Total.Sessions);                       // sessions add across runs
        Assert.Equal(["2026-07", "2026-08"], report.Months.Select(m => m.Label));
        Assert.Equal(32.40m, report.Months.Sum(m => m.Cost) / 2);
        Assert.Equal("agent", report.Categories[0].Label);            // biggest lane first
    }

    // ------------------------------------------------------------------ the archive end of it

    [Fact]
    public void TheArchiveReadsCostRowsPerCategoryFromARealDatabase()
    {
        var db = Path.Combine(_tmp, "run.db");
        Seed(db);

        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);
        var costs = archive!.Costs("R1");

        Assert.Equal(3, costs.Count);
        Assert.Equal("agent", costs[0].Category);
        Assert.Equal(9_000_000, costs[0].TokensCacheRead);
        Assert.Equal(10_000_000, costs[0].Tokens);
        Assert.Equal(8m, costs[0].CostUsd);
        Assert.Equal("advisor", costs[2].Category);
    }

    [Fact]
    public void AMoneyRunReadFromTheArchiveHasTheSameTotalAsTheHandSum()
    {
        var db = Path.Combine(_tmp, "run2.db");
        Seed(db);
        var archive = RunArchive.TryOpen(db)!;

        var run = MoneyAnalyzer.AnalyzeRun("R1", "k43", "repo", null, null,
            archive.Sessions("R1"), archive.Costs("R1"), []);

        Assert.Equal(10_000_000, run.Total.Tokens);
        Assert.Equal(9_000_000, run.Total.CacheReadTokens);
        Assert.Equal(8.35m, run.Total.Cost);              // $8 agent + $0.10 gate + $0.25 advisor
        Assert.Equal(1, run.Total.Checkpoints);
        Assert.Equal(8.35m, run.Total.CostPerCheckpoint);
    }

    // ------------------------------------------------------------------ the report carries it

    [Fact]
    public void TheReportCarriesTheSameNumbersAsTheVerb()
    {
        var run = Run([Window("no ceiling observed", 1, 2), Window("8M / nudge 6M", 3, 4)]);

        var md = Reporter.Build(
            new PlanConfig { Name = "T", Repo = Path.GetTempPath() },
            new RunState { PlanName = "T" }, new TrackerSnapshot(), null, money: run);

        Assert.Contains("## Money", md, StringComparison.Ordinal);
        Assert.Contains("| **run total** | 4 | 40M | 90.0% | $32.40 | 4 | 10M | $8.10 |", md, StringComparison.Ordinal);
        Assert.Contains("| stage K1 |", md, StringComparison.Ordinal);
        Assert.Contains("| 2026-07 |", md, StringComparison.Ordinal);
        Assert.Contains("window 1-2 no ceiling observed", md, StringComparison.Ordinal);
        Assert.Contains("agent $32.00", md, StringComparison.Ordinal);
        Assert.Contains("blended $0.81/M tokens", md, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportSaysNothingAboutMoneyWhenNothingWasSpent()
    {
        var md = Reporter.Build(
            new PlanConfig { Name = "T", Repo = Path.GetTempPath() },
            new RunState { PlanName = "T" }, new TrackerSnapshot(), null);

        Assert.DoesNotContain("## Money", md, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCarriesEveryColumnTheTableShows()
    {
        var json = MoneyJson.Serialize(MoneyAnalyzer.Combine("this repo", [Run([Window("a", 1, 2), Window("b", 3, 4)])]));

        Assert.Contains("\"cacheReadShare\": 0.9", json, StringComparison.Ordinal);
        Assert.Contains("\"costPerCheckpoint\": 8.1", json, StringComparison.Ordinal);
        Assert.Contains("\"costPerMillionTokens\": 0.81", json, StringComparison.Ordinal);
        Assert.Contains("\"capCostPayoff\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stages\"", json, StringComparison.Ordinal);
        Assert.Contains("\"months\"", json, StringComparison.Ordinal);
        Assert.Contains("\"windows\"", json, StringComparison.Ordinal);
    }

    /// <summary>One run, one session, one row per lane — written the way the engine writes them, and
    /// read back through the read-only archive.</summary>
    private static void Seed(string db)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE runs (run_id TEXT PRIMARY KEY, plan_name TEXT NOT NULL, repo TEXT NOT NULL, branch TEXT, " +
            "  driver_ver TEXT, status TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT);" +
            "INSERT INTO runs VALUES ('R1','k43','r',NULL,NULL,'completed','2026-08-01T00:00:00Z',NULL);" +
            "CREATE TABLE sessions (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, stage_id TEXT NOT NULL, " +
            "  number INTEGER NOT NULL, kind TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT, outcome TEXT, " +
            "  agent_session_id TEXT, resume_count INTEGER NOT NULL DEFAULT 0, attempt INTEGER NOT NULL DEFAULT 0, " +
            "  gate_summary TEXT, result_summary TEXT, commit_count INTEGER NOT NULL DEFAULT 0, newly_done TEXT);" +
            "INSERT INTO sessions (run_id, stage_id, number, kind, started_utc, outcome, newly_done) " +
            "  VALUES ('R1','K4',1,'Deliver','2026-08-01T00:00:00Z','Completed','K4.3');" +
            "CREATE TABLE costs (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, session_number INTEGER NOT NULL, " +
            "  category TEXT NOT NULL, tokens_in INTEGER NOT NULL DEFAULT 0, tokens_out INTEGER NOT NULL DEFAULT 0, " +
            "  tokens_think INTEGER NOT NULL DEFAULT 0, tokens_cache INTEGER NOT NULL DEFAULT 0, " +
            "  cost_usd REAL NOT NULL DEFAULT 0, wall_ms INTEGER NOT NULL DEFAULT 0);" +
            "INSERT INTO costs (run_id, session_number, category, tokens_in, tokens_out, tokens_cache, cost_usd) VALUES " +
            "  ('R1',1,'agent',900000,100000,9000000,8.0), ('R1',1,'gate',0,0,0,0.10), ('R1',1,'advisor',0,0,0,0.25);";
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Guards the format the verb prints, not just the arithmetic behind it.</summary>
    [Fact]
    public void TheShareColumnKeepsTheDecimalThatDistinguishesNinetyEightPointFiveFromNinetyEightPointTwo()
    {
        var high = new MoneyLine("a", 1, 1000, 985, 15, 0, 1m, 1);
        var low = new MoneyLine("b", 1, 1000, 982, 18, 0, 1m, 1);

        Assert.Equal("98.5", (high.CacheReadShare * 100).ToString("0.0", CultureInfo.InvariantCulture));
        Assert.Equal("98.2", (low.CacheReadShare * 100).ToString("0.0", CultureInfo.InvariantCulture));
    }
}
