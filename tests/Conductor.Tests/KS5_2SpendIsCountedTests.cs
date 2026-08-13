using Conductor.Core;
using Conductor.Core.Accounting;
using Conductor.Core.History;
using Conductor.Core.Money;
using Conductor.Core.Http;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Hosting;
using Conductor.Http;
using Conductor.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS5.2 — every model this engine spawns is counted, and counted once.
///
/// <para>Eight paths in this codebase start a process that takes a model. Exactly one of them wrote a
/// <c>costs</c> row, and the number it wrote was <c>0.0005 × elapsed seconds</c> — a rate nobody has
/// ever been charged, filed in the same table as figures the provider reported. The other seven —
/// analysis lanes, fix-lanes, the parallel audit, the supervisor hook, the status agent, the auth
/// probe, and the advisor's four non-engine callers — spent real money and reported nothing, which is
/// why a run that had burnt an afternoon of lane time could report the same total as one that had run
/// no lanes at all.</para>
///
/// <para>The two claims worth seeding against a REAL run are here: a lane's billed figure reaches the
/// database with its own category, and the cap can be tripped by spend that is not the delivery
/// agent's. The rest are unit-shaped because they are arithmetic, and arithmetic does not need a
/// process.</para>
/// </summary>
public sealed class KS5_2SpendIsCountedTests : IDisposable
{
    private readonly string _tmp;

    public KS5_2SpendIsCountedTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks52-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ the wire, not the clock

    /// <summary>A real <c>claude -p --output-format stream-json</c> result envelope, of the shape the
    /// SC2.3 rig records: the per-call usages sum to the terminal result, which carries the money.</summary>
    private const string ClaudeStream = """
        {"type":"system","subtype":"init"}
        {"type":"assistant","message":{"id":"msg_01","usage":{"input_tokens":1200,"output_tokens":300,"cache_read_input_tokens":8000},"content":[{"type":"text","text":"looked at the tree"}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"the analysis","total_cost_usd":0.0731,"num_turns":2,"usage":{"input_tokens":1200,"output_tokens":300,"cache_read_input_tokens":8000}}
        """;

    [Fact]
    public void BilledSpend_ReadsWhatTheProviderReported()
    {
        var receipt = BilledSpend.Read(new ClaudeProvider(), SpendCategory.Lane, ClaudeStream, wallMs: 4200);

        Assert.NotNull(receipt);
        Assert.Equal(0.0731m, receipt!.CostUsd);
        Assert.Equal(SpendCategory.Lane, receipt.Category);
        Assert.Equal(1200, receipt.TokensIn);
        Assert.Equal(300, receipt.TokensOut);
        Assert.Equal(8000, receipt.TokensCacheRead);
        Assert.Equal(4200, receipt.WallMs);
    }

    /// <summary>The half that matters more: a backend that reports no money produces NO receipt. A
    /// zero would be indistinguishable from a cheap call, and "we do not know" is the honest answer —
    /// the whole reason the advisor's constant had to go rather than be replaced by a better guess.</summary>
    [Fact]
    public void BilledSpend_SaysNothingWhenTheWireSaidNothing()
    {
        Assert.Null(BilledSpend.Read(new ClaudeProvider(), SpendCategory.Lane,
            "I had a look at the repository and it seems fine.", 9000));
        Assert.Null(BilledSpend.Read(new ClaudeProvider(), SpendCategory.Lane, "", 9000));
        // A result envelope with no total_cost_usd: turns and text, no bill.
        Assert.Null(BilledSpend.Read(new ClaudeProvider(), SpendCategory.Lane,
            """{"type":"result","subtype":"success","result":"done","num_turns":1}""", 9000));
    }

    /// <summary>The elapsed clock is not an input to the figure. Same envelope, wildly different wall
    /// time, same dollars — which is exactly what <c>0.0005 × seconds</c> could not say.</summary>
    [Fact]
    public void BilledSpend_DoesNotDependOnHowLongItTook()
    {
        var quick = BilledSpend.Read(new ClaudeProvider(), SpendCategory.Advisor, ClaudeStream, 120);
        var slow = BilledSpend.Read(new ClaudeProvider(), SpendCategory.Advisor, ClaudeStream, 900_000);

        Assert.Equal(quick!.CostUsd, slow!.CostUsd);
    }

    // ------------------------------------------------------------------ the advisor, off the wire

    /// <summary>The advisor spawn, run for real against a script that speaks the provider's wire, and
    /// asserted to bill what the envelope said. The old path could not have passed this: it never
    /// looked at the envelope, only at a stopwatch.</summary>
    [Fact]
    public async Task Advisor_BillsWhatTheProviderReported()
    {
        var script = WriteAgentScript("advisor.cmd", ClaudeStream);
        var plan = new PlanConfig
        {
            Name = "AdvisorSpend",
            Repo = _tmp,
            Advisor = new AdvisorConfig
            {
                Enabled = true,
                Command = "cmd.exe",
                Args = { "/c", script, "{prompt}" },
                Output = "stream-json",
                TimeoutMinutes = 1,
            },
        };

        var reply = await Advisor.AskAsync(plan, "what should I do?");

        Assert.NotNull(reply.Spend);
        Assert.Equal(0.0731m, reply.Spend!.CostUsd);
        Assert.Equal(SpendCategory.Advisor, reply.Spend.Category);
    }

    // ------------------------------------------------------------------ the ledger

    [Fact]
    public void Ledger_WritesOneRowAndAccruesOnce()
    {
        var db = Path.Combine(_tmp, "ledger", "run.db");
        var accrued = 0m;
        var lines = new List<string>();

        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "LedgerPlan", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            var ledger = new RunSpendLedger(store, "r1", r => accrued += r.CostUsd, lines.Add);

            Assert.True(ledger.Record(
                new SpendReceipt(SpendCategory.Lane, 0.25m, 10, 20, 0, 30, 900), 3, "analysis lane 'x'"));
        }
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db);
        var costs = archive!.Costs("r1");
        Assert.Single(costs);
        Assert.Equal(SpendCategory.Lane, costs[0].Category);
        Assert.Equal(0.25m, costs[0].CostUsd);
        Assert.Equal(3, costs[0].SessionNumber);
        Assert.Equal(0.25m, accrued);
        Assert.Contains(lines, l => l.Contains("counted against the run cap", StringComparison.Ordinal));
    }

    /// <summary>No figure, no row — and the run says so. Recording a zero here is the failure mode the
    /// whole checkpoint is about: an unknown rendered as "it was free".</summary>
    [Fact]
    public void Ledger_RecordsNothingWhenThereIsNoBilledFigure()
    {
        var db = Path.Combine(_tmp, "silent", "run.db");
        var accrued = 0m;
        var lines = new List<string>();

        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "SilentPlan", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            Assert.False(new RunSpendLedger(store, "r1", r => accrued += r.CostUsd, lines.Add)
                .Record(null, 1, "supervisor hook"));
        }
        SqliteConnection.ClearAllPools();

        Assert.Empty(RunArchive.TryOpen(db)!.Costs("r1"));
        Assert.Equal(0m, accrued);
        Assert.Contains(lines, l => l.Contains("unknown, not zero", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what the cap compares

    /// <summary>One total, two readers. The cap parks on <see cref="RunState.BilledWindowCostUsd"/> and
    /// <c>/state</c> serves the same sum as <c>costSpent</c>, so an operator watching the dashboard and
    /// the run that stops cannot be looking at different money.</summary>
    [Fact]
    public void TheCapTotalAndTheStateBlockAgree()
    {
        var plan = new PlanConfig { Name = "CapPlan", Repo = _tmp };
        plan.Limits.MaxRunCostUsd = 1.00m;
        var state = new RunState
        {
            PlanName = "CapPlan",
            PerRunCostUsd = 0.40m,
            PerRunSideCostUsd = 0.30m,
            TotalSideCostUsd = 0.30m,
        };
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "S1", StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            EndedUtc = DateTime.UtcNow, CostUsd = 0.40m,
        });

        Assert.Equal(0.70m, state.BilledWindowCostUsd);
        Assert.Equal(0.70m, state.BilledLifetimeCostUsd);

        var dto = ControlPlaneServer.WithBudget(
            ControlPlaneMapper.FromSnapshot(
                SnapshotBuilder.Build(plan, state, new TrackerSnapshot()), "r1", plan.Repo, plan.PlanDir),
            plan.Limits, state);

        Assert.Equal(state.BilledWindowCostUsd, dto.CostSpent);
        Assert.Equal(state.BilledWindowCostUsd, dto.WindowCostUsd);
        Assert.Equal(0.30m, dto.CostRemaining);
        // The invariant that makes the two figures readable together.
        Assert.True(dto.WindowCostUsd <= dto.LifetimeCostUsd,
            $"window ${dto.WindowCostUsd} must never exceed lifetime ${dto.LifetimeCostUsd}");
    }

    /// <summary>KS5.3's floor is measured off the AGENT stream. New categories must not move it: the
    /// session ceiling is compared against the agent's tokens, so a floor measured over lane tokens
    /// would sit above the number the rail actually enforces.</summary>
    [Fact]
    public void NewCategoriesDoNotMoveTheSessionsCapTokens()
    {
        var db = Path.Combine(_tmp, "captokens", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "CapTokens", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            store.InitializeStage("r1", "S1", "First");
            store.RecordSession("r1", "S1", 1, "work", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow,
                "advance", null, 0, 1, "ok", "did the thing", 1, "C1");
            store.RecordCost("r1", 1, SpendCategory.Agent, 1_000, 500, 0, 4_000, 0.50m, 1_000);
            store.RecordCost("r1", 1, SpendCategory.Lane, 900_000, 100, 0, 0, 0.20m, 1_000);
        }
        SqliteConnection.ClearAllPools();

        var session = RunArchive.TryOpen(db)!.Sessions("r1").Single();

        Assert.Equal(5_500, session.CapTokens);
        Assert.Equal(905_600, session.Tokens);
    }

    // ------------------------------------------------------------------ what `money` renders

    /// <summary>The money verb groups by whatever category it finds, so the new lanes render without
    /// anyone teaching it their names — and the run total still equals the sum of the rows.</summary>
    [Fact]
    public void MoneyRendersEveryCategoryAndTheyStillSumToTheTotal()
    {
        var started = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        List<ArchivedSession> sessions =
        [
            new(1, "S1", "work", started.ToString("O"), started.AddMinutes(30).ToString("O"),
                "advance", 1, 0, 1, 0.50m, 5_500, "did it", "ok", NewlyDone: "C1", AgentTokens: 5_500),
        ];
        List<ArchivedCost> costs =
        [
            new(1, SpendCategory.Agent, 1_000, 500, 0, 4_000, 0.50m, 1_000),
            new(1, SpendCategory.Lane, 800, 200, 0, 0, 0.20m, 900),
            new(1, SpendCategory.Advisor, 100, 50, 0, 0, 0.05m, 400),
            new(1, SpendCategory.Audit, 700, 100, 0, 0, 0.15m, 800),
            new(0, SpendCategory.AuthProbe, 5, 2, 0, 0, 0.01m, 120),
        ];

        var run = MoneyAnalyzer.AnalyzeRun("r1", "MoneyPlan", "money-plan",
            started.ToString("O"), started.AddHours(1).ToString("O"), sessions, costs, []);

        var labels = run.Categories.Select(c => c.Label).ToList();
        Assert.Contains(SpendCategory.Lane, labels, StringComparer.Ordinal);
        Assert.Contains(SpendCategory.Advisor, labels, StringComparer.Ordinal);
        Assert.Contains(SpendCategory.Audit, labels, StringComparer.Ordinal);
        Assert.Contains(SpendCategory.AuthProbe, labels, StringComparer.Ordinal);
        Assert.Equal(run.Total.Cost, run.Categories.Sum(c => c.Cost));
        Assert.Equal(0.91m, run.Total.Cost);
    }

    /// <summary>KS5.2 put cost rows in front of session rows for the first time: the auth probe bills
    /// at run start, and a lane can finish before the first session does. The budget analyzer measured
    /// its fallback window over "every session there is" and indexed <c>[0]</c> — so on such a run it
    /// threw, and took REPORT.md's whole money section down with it (measured on the rig: "report write
    /// failed: Index was out of range"). A report must never be lost to a measurement it could not
    /// take.</summary>
    [Fact]
    public void PricingARunThatHasSpentBeforeItHasASessionDoesNotThrow()
    {
        var db = Path.Combine(_tmp, "nosessions", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "NoSessions", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            store.RecordCost("r1", 0, SpendCategory.AuthProbe, 5, 2, 0, 0, 0.01m, 120);
        }
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db)!;
        var profile = Conductor.Core.Budget.BudgetAnalyzer.Analyze(
            "r1", "NoSessions", archive.Sessions("r1"), archive.SoftBreaks("r1"));

        Assert.Equal(0, profile.Current.Sessions);
        Assert.Equal(0, profile.Current.Closers);
        Assert.Contains("no floor to measure", string.Join(" ", profile.Prescription.Findings), StringComparison.Ordinal);

        var run = MoneyAnalyzer.AnalyzeRun("r1", "NoSessions", "repo", null, null,
            archive.Sessions("r1"), archive.Costs("r1"), profile.Windows);
        Assert.Equal(0.01m, run.Total.Cost);
    }

    /// <summary>Two spellings of one lane are two rows in "where the money goes". The vocabulary is a
    /// set, and this is the test that keeps it one.</summary>
    [Fact]
    public void TheCategoryVocabularyIsDistinct()
        => Assert.Equal(SpendCategory.All.Count, SpendCategory.All.Distinct(StringComparer.Ordinal).Count());

    // ------------------------------------------------------------------ a real run, a real lane

    /// <summary>
    /// The one that had to be a live run: an analysis lane spawned by the engine, billed by the wire,
    /// written to <c>run.db</c> under its own category, and counted by the ceiling.
    /// <para>The cap is set BETWEEN one invocation's bill and two: the delivery session alone stays
    /// under it, and only when the lane's row is counted too does the run park. Before KS5.2 this run
    /// would have finished — the lane's dollars existed, and nothing in the engine had a place to put
    /// them.</para>
    /// </summary>
    [Fact]
    public async Task AnalysisLaneSpend_IsRecordedUnderItsOwnCategoryAndTripsTheRunCap()
    {
        var repo = NewRepo("laneRig");
        var script = WriteAgentScript("claude-agent.cmd", ClaudeStream);

        var plan = new PlanConfig
        {
            Name = "LaneSpend",
            Repo = repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "L0", Title = "Lane rig", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", script, "{prompt}" },
                Provider = "claude",
            },
            AnalysisLanes =
            {
                new AnalysisLaneConfig
                {
                    Id = "risk", Kind = "analysis", Name = "Risk read",
                    Prompt = "What is risky here?", StageTrigger = "L0", TimeoutMinutes = 2,
                },
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;
        // One invocation bills $0.0731. The session's own spend stays under this; the session plus the
        // lane does not. That gap is the whole assertion.
        plan.Limits.MaxRunCostUsd = 0.10m;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        // The park is a park: the loop idles on it rather than returning, exactly as it does in the
        // field, so the run is stopped by the clock and then asked what it decided.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

        Assert.Equal(RunStatus.AwaitingOwner, state.Status);
        Assert.Equal(AwaitingOwnerReason.Budget, state.AwaitingOwnerReason);

        // The agent's own spend was NOT enough to park this run — the lane's is what carried it over.
        Assert.True(state.PerRunCostUsd < plan.Limits.MaxRunCostUsd,
            $"agent spend ${state.PerRunCostUsd} should be under the ${plan.Limits.MaxRunCostUsd} cap on its own");
        Assert.True(state.PerRunSideCostUsd > 0, "the lane's billed spend never reached the budget");
        Assert.True(state.BilledWindowCostUsd >= plan.Limits.MaxRunCostUsd);

        SqliteConnection.ClearAllPools();
        var costs = RunArchive.TryOpen(plan.RunDbPath)!.Costs(state.RunId);
        var lane = Assert.Single(costs, c => c.Category == SpendCategory.Lane);
        Assert.Equal(0.0731m, lane.CostUsd);
        Assert.Equal(8_000, lane.TokensCacheRead);
        Assert.Contains(costs, c => c.Category == SpendCategory.Agent);
    }

    // ------------------------------------------------------------------ fixtures

    private string WriteAgentScript(string name, string ndjson)
    {
        var path = Path.Combine(_tmp, name);
        var lines = new List<string> { "@echo off" };
        foreach (var line in ndjson.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) lines.Add("echo " + t);
        }
        lines.Add("exit /b 0");
        lines.Add("");
        File.WriteAllText(path, string.Join("\r\n", lines));
        return path;
    }

    private string NewRepo(string name)
    {
        var repo = Path.Combine(_tmp, name);
        Directory.CreateDirectory(repo);
        Git("init", "-b", "main");
        Git("config", "user.email", "ks52@test");
        Git("config", "user.name", "KS5.2 Test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "# KS5.2 lane rig");
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# Lane rig\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| L0.1 | lane rig checkpoint | TODO | | |\n");
        Git("add", "-A");
        Git("commit", "-m", "chore: initial commit", "--no-gpg-sign");
        return repo;

        void Git(params string[] args)
        {
            var r = ProcessRunner.Run("git", args, repo, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed: {r.Output} {r.StdErr}");
        }
    }
}
