using System.Text.Json;
using Conductor.Commands;
using Conductor.Core.History;
using Conductor.Core.Money;
using Conductor.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS5.1 — <c>conductor spend</c>: what this MACHINE spent, this week and this month, across every
/// store it knows about.
///
/// <para>Two claims are worth more than the rest, and both are seeded here against real databases
/// written by the real writer. First: the window is a <b>session's</b> start, not a run's. The
/// <c>--since</c> filter every other verb shares keeps or drops a whole run by its last activity, so
/// a run that started in July and closed a checkpoint this morning reports its entire July bill as
/// "this week". Second: each real run is counted <b>once</b>. The catalogue has minted duplicates on
/// this machine before — KS0.1 measured one <c>run.db</c> living in five stores, 37 rows for 25 runs
/// — so a machine total keyed on catalogue entries is wrong by however many copies the index holds.</para>
///
/// <para>Every state home here is a temp directory. Nothing in this file reads or writes the
/// operator's real catalogue.</para>
/// </summary>
public sealed class KS5_1MachineLedgerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    /// <summary>A fixed "now" so "this week" is a fact rather than a function of the wall clock.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    public KS5_1MachineLedgerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks51-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Writes a run through the real store — sessions and billed cost rows — and catalogues
    /// it, so what the ledger reads is what the engine actually records.</summary>
    private string SeedRun(string repo, string plan, string runId, params (DateTime Started, decimal Cost)[] sessions)
    {
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", Conductor.Core.EngineStamp.Parse("0.4.0+ks51"));
            store.InitializeStage(runId, "S1", "First stage");
            for (var i = 0; i < sessions.Length; i++)
            {
                var (started, cost) = sessions[i];
                store.RecordSession(runId, "S1", i + 1, "work", started, started.AddMinutes(30), "advance",
                    agentSessionId: null, resumeCount: 0, attempt: 1,
                    gateSummary: "ok", resultSummary: $"session {i + 1}", commitCount: 1,
                    newlyDone: $"C{i + 1}");
                store.RecordCost(runId, i + 1, "agent", 1_000, 2_000, 0, 7_000, cost, 1_000);
            }
            store.RecordRunEnd(runId, "completed");
        }
        StateCatalogue.Upsert(_root, repo, plan, db);
        SqliteConnection.ClearAllPools();
        return db;
    }

    private string RepoPath(string name)
    {
        var p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static DateTime Utc(int day, int hour = 9) => new(2026, 8, day, hour, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<MachineLedgerWindow> Week() =>
        [new MachineLedgerWindow("this week (7d)", Now.AddDays(-7), null)];

    /// <summary>The verb's own path: resolve, read, measure, roll up.</summary>
    private MachineLedgerReport Ledger(IReadOnlyList<MachineLedgerWindow>? windows = null)
        => SpendCommand.Measure(_root, windows ?? Week(), "this machine");

    // ------------------------------------------------------------------ the window is a session's

    [Fact]
    public void ARunStraddlingTheWindowContributesOnlyTheSessionsInsideIt()
    {
        // Sessions on the 1st and the 3rd are outside the last seven days; the 12th is inside. The
        // run's LAST ACTIVITY is inside, which is exactly the shape that makes a whole-run filter lie.
        SeedRun(RepoPath("straddle"), "core", "run-straddle-01",
            (Utc(1), 4.00m), (Utc(3), 2.00m), (Utc(12), 1.50m));

        var report = Ledger();

        Assert.Equal(1.50m, report.Periods[0].Cost);      // only the in-window session
        Assert.Equal(1, report.Periods[0].Sessions);
        Assert.Equal(7.50m, report.Total.Cost);           // the lifetime is still the whole run
        Assert.Equal(3, report.Total.Sessions);
    }

    [Fact]
    public void TheWholeRunSinceFilterWouldHaveAttributedTheWholeLifetimeToTheWeek()
    {
        // The failure clause 2 exists to prevent, demonstrated rather than asserted in prose: the
        // shared RunHistory filter keeps this run in a 7-day window and keeps ALL of it.
        SeedRun(RepoPath("straddle2"), "core", "run-straddle-02",
            (Utc(1), 4.00m), (Utc(12), 1.50m));

        var wholeRun = RunHistory.List(_root, new RunHistoryFilter(Since: Now.AddDays(-7)));
        Assert.Single(wholeRun);
        Assert.Equal(5.50m, wholeRun[0].Run!.CostUsd);   // the run row's lifetime cost, all of it

        Assert.Equal(1.50m, Ledger().Periods[0].Cost);   // what the week actually cost
    }

    [Fact]
    public void AWindowAndItsComplementAndTheUndatedRowsPartitionTheRunExactly()
    {
        SeedRun(RepoPath("partition"), "core", "run-partition-01",
            (Utc(1), 4.00m), (Utc(3), 2.00m), (Utc(12), 1.50m), (Utc(13, 6), 0.25m));

        var inside = new MachineLedgerWindow("inside", Now.AddDays(-7), null);
        var before = new MachineLedgerWindow("before", DateTimeOffset.MinValue, Now.AddDays(-7));
        var report = Ledger([inside, before]);

        Assert.Equal(1.75m, report.Periods[0].Cost);
        Assert.Equal(6.00m, report.Periods[1].Cost);
        Assert.Equal(0m, report.Undated.Cost);
        Assert.Equal(report.Total.Cost, report.Periods[0].Cost + report.Periods[1].Cost + report.Undated.Cost);
    }

    [Fact]
    public void TheDefaultLadderAsksTodayThisWeekAndThisMonthInUtc()
    {
        var ladder = MachineLedger.Ladder(Now);

        Assert.Equal(["today", "this week (7d)", "this month"], ladder.Select(w => w.Label));
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero), ladder[0].Since);
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero), ladder[1].Since);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), ladder[2].Since);
        Assert.True(ladder[0].Contains(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(ladder[0].Contains(new DateTimeOffset(2026, 8, 12, 23, 59, 59, TimeSpan.Zero)));
    }

    // ------------------------------------------------------------------ counted once

    [Fact]
    public void TwoCatalogueEntriesPointingAtOneStoreCountTheRunOnce()
    {
        var repo = RepoPath("dup");
        var db = SeedRun(repo, "core", "run-dup-0001", (Utc(12), 3.00m));
        // Exactly the shape KS0.1 repaired: the same database catalogued again under a second plan
        // slug. Two entries, two history rows, one run.
        StateCatalogue.Upsert(_root, repo, "core-second-plan", db);

        Assert.Equal(2, StateCatalogue.Read(_root).Count);
        Assert.Equal(2, RunHistory.List(_root).Count);

        var report = Ledger();

        Assert.Single(report.Runs);
        Assert.Equal(1, report.DuplicateRunsCollapsed);
        Assert.Equal(3.00m, report.Total.Cost);
        Assert.Equal(1, report.Stores);
    }

    [Fact]
    public void TheSameRunIdInTwoStoresIsCountedOnceAndTheFullerCopyWins()
    {
        // Two stores, one run id: the shape a copy taken mid-run leaves behind. Counting both would
        // double the machine's bill; keeping the shorter one would under-report a finished run.
        var stale = SeedRun(RepoPath("copied-mid-run"), "core", "run-copied-001", (Utc(12), 3.00m));
        var full = SeedRun(RepoPath("copied"), "core", "run-copied-001", (Utc(12), 3.00m), (Utc(13), 1.00m));

        var report = Ledger();

        Assert.Single(report.Runs);
        Assert.Equal(1, report.DuplicateRunsCollapsed);
        Assert.Equal(4.00m, report.Total.Cost);              // the complete copy, not the truncated one
        Assert.True(string.Equals(full, report.Runs[0].DbPath, StringComparison.OrdinalIgnoreCase),
            $"kept {report.Runs[0].DbPath}, expected the fuller copy at {full} (the truncated one is {stale})");
    }

    [Fact]
    public void EveryCatalogueStoreIsRead()
    {
        SeedRun(RepoPath("alpha"), "core", "run-alpha-0001", (Utc(12), 2.00m));
        SeedRun(RepoPath("beta"), "core", "run-beta-0001", (Utc(12), 1.25m));

        var report = Ledger();

        Assert.Equal(2, report.Runs.Count);
        Assert.Equal(2, report.Stores);
        Assert.Equal(0, report.DuplicateRunsCollapsed);
        Assert.Equal(3.25m, report.Total.Cost);
        Assert.Equal(3.25m, report.Periods[0].Cost);
    }

    // ------------------------------------------------------------------ it agrees with `money`

    [Fact]
    public void TheMachineTotalIsTheSumOfWhatMoneyReportsPerRunToTheCent()
    {
        SeedRun(RepoPath("m1"), "core", "run-m1-000001", (Utc(1), 4.00m), (Utc(12), 1.50m));
        SeedRun(RepoPath("m2"), "core", "run-m2-000001", (Utc(11), 2.25m));

        var report = Ledger();

        // What `conductor money --run <id> --json` prints for each run, summed: the same MoneyRun
        // record, produced by the same function, so this is equality and not approximation.
        var perRun = report.Runs.Sum(r => r.Run.Total.Cost);
        Assert.Equal(perRun, report.Total.Cost);
        Assert.Equal(7.75m, report.Total.Cost);

        // And through the serializers, which is where a rounding difference would actually show up.
        var ledgerTotal = Total(MoneyJson.SerializeLedger(report), "total");
        var moneyTotal = report.Runs.Sum(r => Total(MoneyJson.Serialize(
            MoneyAnalyzer.Combine("one run", [r.Run])), "total"));
        Assert.Equal(ledgerTotal, moneyTotal);
        Assert.Equal(7.75m, ledgerTotal);
    }

    [Fact]
    public void JsonCarriesTheSameFiguresTheTablePrints()
    {
        SeedRun(RepoPath("j1"), "core", "run-json-00001", (Utc(1), 4.00m), (Utc(12), 1.50m));

        var report = Ledger(MachineLedger.Ladder(Now));
        var json = MoneyJson.SerializeLedger(report);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(report.Total.Cost, root.GetProperty("total").GetProperty("costUsd").GetDecimal());
        Assert.Equal(report.Runs.Count, root.GetProperty("runs").GetInt32());
        Assert.Equal(report.Stores, root.GetProperty("stores").GetInt32());
        Assert.False(root.GetProperty("nothingRecorded").GetBoolean());
        Assert.Equal(
            report.Periods.Select(p => p.Label),
            root.GetProperty("periods").EnumerateArray().Select(p => p.GetProperty("label").GetString()));
        Assert.Equal(
            report.Periods.Select(p => p.Cost),
            root.GetProperty("periods").EnumerateArray().Select(p => p.GetProperty("costUsd").GetDecimal()));
        Assert.Equal("run-json-00001",
            root.GetProperty("perRun")[0].GetProperty("runId").GetString());
    }

    private static decimal Total(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(property).GetProperty("costUsd").GetDecimal();
    }

    // ------------------------------------------------------------------ undated, and nothing at all

    [Fact]
    public void ACostRowWhoseSessionHasNoDateIsBucketedUndatedAndStaysInTheTotal()
    {
        // The row a torn write or an older schema leaves behind: billed, but joined to no session, so
        // no window can honestly claim it. Dropping it would make the machine total quietly wrong.
        IReadOnlyList<ArchivedSession> sessions =
            [new(1, "S1", "work", "2026-08-12T09:00:00Z", null, "advance", 1, 0, 1, 0m, 0, null, null, NewlyDone: "C1")];
        IReadOnlyList<ArchivedCost> costs =
            [new(1, "agent", 1_000, 2_000, 0, 7_000, 1.50m, 0), new(99, "advisor", 0, 0, 0, 0, 0.25m, 0)];

        var measured = MachineLedger.Measure("db", "R1", "core", "repo", null, null, sessions, costs, Week());
        var report = MachineLedger.Build("this machine", _root, Week(), [measured]);

        Assert.Equal(1.75m, report.Total.Cost);          // the undated row is in the lifetime
        Assert.Equal(1.50m, report.Periods[0].Cost);     // and in none of the windows
        Assert.Equal(0.25m, report.Undated.Cost);
        Assert.Equal("undated", report.Undated.Label);
    }

    [Fact]
    public void ASessionWhoseStartWillNotParseIsUndatedRatherThanNow()
    {
        IReadOnlyList<ArchivedSession> sessions =
            [new(1, "S1", "work", "last tuesday", null, "advance", 1, 0, 1, 0m, 0, null, null, NewlyDone: "C1")];
        IReadOnlyList<ArchivedCost> costs = [new(1, "agent", 0, 0, 0, 0, 2.00m, 0)];

        var measured = MachineLedger.Measure("db", "R1", "core", "repo", null, null, sessions, costs, Week());

        Assert.Equal(0m, measured.Periods[0].Cost);
        Assert.Equal(2.00m, measured.Undated.Cost);
        Assert.Equal(1, measured.Undated.Checkpoints);   // the delivery travels with the money
    }

    [Fact]
    public void AMachineWithNoCatalogueAndNoLocalDatabaseSaysNothingRecorded()
    {
        var empty = Path.Combine(_tmp, "empty-home");
        Directory.CreateDirectory(empty);

        var report = SpendCommand.Measure(empty, MachineLedger.Ladder(Now), "this machine");

        Assert.True(report.NothingRecorded);
        Assert.Empty(report.Runs);
        Assert.Equal(0m, report.Total.Cost);
        Assert.Equal(3, report.Periods.Count);           // the question is still answered, with zeroes
        Assert.All(report.Periods, p => Assert.Equal(0m, p.Cost));
        Assert.True(JsonDocument.Parse(MoneyJson.SerializeLedger(report))
            .RootElement.GetProperty("nothingRecorded").GetBoolean());
    }

    // ------------------------------------------------------------------ the house rule

    [Fact]
    public void TheLedgerNeverPricesATokenItOnlySumsBilledRows()
    {
        // Every dollar on the report has to be traceable to a costs row. Seed tokens with NO cost and
        // a cost with NO tokens: a ledger that modelled anything would report a dollar for the first.
        IReadOnlyList<ArchivedSession> sessions =
            [new(1, "S1", "work", "2026-08-12T09:00:00Z", null, "advance", 1, 0, 1, 0m, 0, null, null, NewlyDone: "C1")];
        IReadOnlyList<ArchivedCost> costs =
            [new(1, "agent", 500_000, 500_000, 0, 9_000_000, 0m, 0), new(1, "gate", 0, 0, 0, 0, 0.10m, 0)];

        var measured = MachineLedger.Measure("db", "R1", "core", "repo", null, null, sessions, costs, Week());

        Assert.Equal(10_000_000, measured.Periods[0].Tokens);
        Assert.Equal(0.10m, measured.Periods[0].Cost);   // the billed row, and nothing else
        Assert.Equal(0.10m, measured.Run.Total.Cost);
    }
}
