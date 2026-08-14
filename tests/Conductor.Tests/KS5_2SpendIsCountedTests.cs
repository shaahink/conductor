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

    /// <summary>The same wire, minus the money: a successful turn whose result envelope carries no
    /// <c>total_cost_usd</c>. A backend that reports nothing is not a backend that charged nothing, and
    /// the engine must record no row for it — which also makes it the fixture for a run whose only
    /// billed spend is somebody other than the delivery agent's.</summary>
    private const string UnbilledStream = """
        {"type":"system","subtype":"init"}
        {"type":"assistant","message":{"id":"msg_02","usage":{"input_tokens":40,"output_tokens":12},"content":[{"type":"text","text":"delivered"}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"delivered","num_turns":1,"usage":{"input_tokens":40,"output_tokens":12}}
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

    /// <summary>The other half of the floor's honesty, and the re-verification catch: a session with
    /// NO agent row at all — the agent's row is only written when its provider reported a figure
    /// (<c>RunLoop.Plumbing</c>), so this is any session whose only spend is lanes, advisors or gates.
    /// <c>CapTokens</c> used to fall back to the ALL-category sum for it, which handed 900k of lane
    /// tokens to a floor the session ceiling never governs. The honest value is 0: no agent stream was
    /// measured, and <c>BudgetAnalyzer</c> reads a zero as "unmeasured", not as a cheap session.</summary>
    [Fact]
    public void ASessionWithOnlyNonAgentRowsMeasuresZeroCapTokens()
    {
        var db = Path.Combine(_tmp, "noagentrow", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "NoAgentRow", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            store.InitializeStage("r1", "S1", "First");
            store.RecordSession("r1", "S1", 1, "work", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow,
                "advance", null, 0, 1, "ok", "unbilled agent, busy lanes", 1, "C1");
            store.RecordCost("r1", 1, SpendCategory.Lane, 900_000, 100, 0, 0, 0.20m, 1_000);
            store.RecordCost("r1", 1, SpendCategory.Advisor, 4_000, 400, 0, 0, 0.05m, 700);
            store.RecordCost("r1", 1, SpendCategory.Gate, 0, 0, 0, 0, 0.02m, 0);
        }
        SqliteConnection.ClearAllPools();

        var session = RunArchive.TryOpen(db)!.Sessions("r1").Single();

        Assert.Equal(0, session.CapTokens);
        Assert.Equal(904_500, session.Tokens);
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

    // ------------------------------------------------------------------ spend from another writer

    /// <summary>The query the cap now reads a second writer's rows through. Billed and not the agent's:
    /// the gate row is an estimate priced from <c>limits.overheadCostPerSecond</c>, and the agent's own
    /// row is already in <c>PerRunCostUsd</c> — counting either here would double the total the run
    /// parks on.</summary>
    [Fact]
    public void SideSpendIsEveryBilledRowThatIsNotTheAgents()
    {
        var db = Path.Combine(_tmp, "side", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "SidePlan", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            store.RecordCost("r1", 1, SpendCategory.Agent, 10, 5, 0, 0, 1.00m, 100);
            store.RecordCost("r1", 1, SpendCategory.Gate, 0, 0, 0, 0, 0.50m, 0);
            store.RecordCost("r1", 1, SpendCategory.Lane, 10, 5, 0, 0, 0.25m, 100);
            store.RecordCost("r1", 1, SpendCategory.Supervisor, 10, 5, 0, 0, 0.10m, 100);
            store.RecordCost("r1", 0, SpendCategory.AuthProbe, 1, 1, 0, 0, 0.05m, 100);
            store.RecordCost("r2", 1, SpendCategory.Lane, 10, 5, 0, 0, 9.99m, 100);

            Assert.Equal(0.40m, store.SumSideSpendUsd("r1"));
            Assert.Equal(0m, store.SumSideSpendUsd("nobody"));
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The supervisor writes from a DIFFERENT process and the control plane from an HTTP
    /// thread, so neither may touch the engine's in-memory counters. Both used to stop at the row and
    /// say the cap would see it "the next time the run is priced from its database" — and nothing ever
    /// priced a run from its database, so those dollars could not reach a ceiling at all. This is the
    /// arithmetic that made the sentence true: the table minus what the engine has already counted.
    /// </summary>
    [Fact]
    public void ARowWrittenOutsideTheLoopIsTheDifferenceTheEngineHasYetToCount()
    {
        var db = Path.Combine(_tmp, "external", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "ExternalPlan", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));

            // The engine's own ledger: row AND accrual, on the loop thread.
            var engineAccrued = 0m;
            var engine = new RunSpendLedger(store, "r1", r => engineAccrued += r.CostUsd);
            engine.Record(new SpendReceipt(SpendCategory.Lane, 0.25m, 10, 20, 0, 30, 900), 1, "analysis lane 'x'");

            // The `watch` supervisor's shape: no accrue callback at all — it cannot move counters that
            // live in another process's memory.
            new RunSpendLedger(store, "r1")
                .Record(new SpendReceipt(SpendCategory.Supervisor, 0.10m, 5, 5, 0, 0, 400), 1, "supervisor hook");

            Assert.Equal(0.25m, engineAccrued);
            Assert.Equal(0.35m, store.SumSideSpendUsd("r1"));
            // What the boundary absorbs: the supervisor's dime, exactly once.
            Assert.Equal(0.10m, store.SumSideSpendUsd("r1") - engineAccrued);
            Assert.Equal(0m, store.SumSideSpendUsd("r1") - (engineAccrued + 0.10m));
        }
        SqliteConnection.ClearAllPools();
    }

    // ------------------------------------------------------------------ the auth probe

    /// <summary>The one-token credential ping runs on EVERY run start and was the only model spawn that
    /// contributed to no total at all. It is also the riskiest arm of this checkpoint's diff — it is the
    /// path that first billed before a run had a session row, which is what crashed
    /// <c>BudgetAnalyzer.Measure</c> — so it is proved by running it, not by describing it.
    /// <para><see cref="AuthSmokeTest.CanProbe"/> only fires for a recognised provider CLI, which is why
    /// the fake here is NAMED <c>claude-probe.cmd</c> rather than invoked through <c>cmd.exe</c>: with
    /// the shell in front, the probe skips and this test would assert nothing.</para></summary>
    [Fact]
    public async Task AuthProbe_BillsItsPingUnderItsOwnCategoryAndKeysItToSessionZero()
    {
        var script = WriteAgentScript("claude-probe.cmd", ClaudeStream);
        var plan = new PlanConfig
        {
            Name = "ProbeSpend",
            Repo = _tmp,
            Agent = new AgentConfig { Command = script, Args = { "{prompt}" }, Provider = "claude" },
        };
        Assert.True(AuthSmokeTest.CanProbe(plan.Agent), "the fixture must be a probe-able provider CLI");

        var calls = 0;
        SpendReceipt? seen = null;
        var result = await AuthSmokeTest.RunAsync(plan, TimeSpan.FromSeconds(60), CancellationToken.None,
            onSpend: r => { calls++; seen = r; });

        Assert.True(result.Passed, result.Message);
        Assert.Equal(1, calls);
        Assert.NotNull(seen);
        Assert.Equal(SpendCategory.AuthProbe, seen!.Category);
        Assert.Equal(0.0731m, seen.CostUsd);

        // Session 0 is the key the ledger chose out loud: the probe bills BEFORE session 1 exists, and
        // `costs.session_number` is NOT NULL, so the alternative was dropping the row.
        var db = Path.Combine(_tmp, "probe", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("r1", "ProbeSpend", _tmp, "main", EngineStamp.Parse("0.4.0+ks52"));
            Assert.True(new RunSpendLedger(store, "r1").Record(seen, 0, "auth preflight probe"));
        }
        SqliteConnection.ClearAllPools();

        var row = Assert.Single(RunArchive.TryOpen(db)!.Costs("r1"));
        Assert.Equal(SpendCategory.AuthProbe, row.Category);
        Assert.Equal(0, row.SessionNumber);
        Assert.Equal(0.0731m, row.CostUsd);
    }

    /// <summary>The ordering, proved by a real engine rather than by a comment. <c>RestoreBudget</c>
    /// OVERWRITES the live counters from run state, so a probe that accrued before it ran was wiped a
    /// line later and the run started the session believing it had spent nothing. Here the run resumes
    /// carrying $0.0200 of prior side spend: if the restore came second, the probe's bill would be gone
    /// and the total would still read $0.0200.</summary>
    [Fact]
    public async Task AuthProbeSpend_SurvivesTheBudgetRestoreItUsedToBeWipedBy()
    {
        var repo = NewRepo("probeRig");
        var script = WriteAgentScript("claude-agent.cmd", ClaudeStream);
        var plan = RigPlan("ProbeRig", repo,
            new AgentConfig { Command = script, Args = { "{sessionId}", "{prompt}" }, Provider = "claude" });

        // A prior process's lane spend, exactly as RestoreBudget would find it on a resume.
        var state = new RunState
        {
            RunId = Guid.NewGuid().ToString("N"),
            PerRunSideCostUsd = 0.0200m,
            TotalSideCostUsd = 0.0200m,
        };

        using (var host = ConductorHost.Build(plan, state, new PlainSink(),
                   new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token); }
            catch (OperationCanceledException) { }
        }
        SqliteConnection.ClearAllPools();

        var costs = RunArchive.TryOpen(plan.RunDbPath)!.Costs(state.RunId);
        var probe = Assert.Single(costs, c => c.Category == SpendCategory.AuthProbe);
        Assert.Equal(0, probe.SessionNumber);
        Assert.Equal(0.0731m, probe.CostUsd);
        Assert.True(state.PerRunSideCostUsd >= 0.0931m,
            $"the probe's ${probe.CostUsd} was wiped by RestoreBudget — side spend is ${state.PerRunSideCostUsd}, " +
            $"expected the restored $0.0200 plus the probe.\n{Diagnose(plan, state)}");
    }

    // ------------------------------------------------------------------ a real run, a real lane

    /// <summary>
    /// The one that had to be a live run: an analysis lane spawned by the engine, billed by the wire,
    /// written to <c>run.db</c> under its own category, and counted by the ceiling.
    /// <para>The run's delivery agent bills NOTHING — its result envelope carries no
    /// <c>total_cost_usd</c> — so what trips <c>limits.maxRunCostUsd</c> here is spend that is not the
    /// agent's, which is the acceptance clause word for word. A supervisor row written the way
    /// <c>conductor watch</c> writes it (another process, no accrual) is seeded into the database
    /// before the engine starts, and the run absorbs it at the session boundary.</para>
    /// <para>Deliberately NOT balanced on two spawns both landing. The first version of this test set
    /// the cap between one invocation's bill and two, so it only passed when the session AND the lane
    /// both billed — a lane that errored under load left the total under the cap, the run finished
    /// instead of parking, and the assertion read <c>Idle</c> as a pass-shaped failure. One decisive
    /// spawn, and every way it can go missing is named in the failure message.</para>
    /// </summary>
    [Fact]
    public async Task AnalysisLaneSpend_IsRecordedUnderItsOwnCategoryAndTripsTheRunCap()
    {
        var repo = NewRepo("laneRig");
        // %1 is the lane id for a lane and the session id for the delivery session: the lane's
        // invocation reports what it was billed, the session's reports no figure at all.
        var script = WriteBillingByCallerScript("claude-agent.cmd", billsWhenArgIs: "risk");

        var plan = RigPlan("LaneSpend", repo,
            new AgentConfig { Command = "cmd.exe", Args = { "/c", script, "{sessionId}", "{prompt}" }, Provider = "claude" });
        plan.AnalysisLanes.Add(new AnalysisLaneConfig
        {
            Id = "risk", Kind = "analysis", Name = "Risk read",
            Prompt = "What is risky here?", StageTrigger = "L0", TimeoutMinutes = 2,
        });
        // Under one lane invocation's $0.0731, so the lane's row alone carries the run over.
        plan.Limits.MaxRunCostUsd = 0.05m;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        SeedSupervisorRowFromAnotherProcess(plan, state.RunId, 0.0100m);

        using (var host = ConductorHost.Build(plan, state, new PlainSink(),
                   new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var run = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            // A budget park does not RETURN — the loop idles on it, exactly as it does in the field —
            // so the park is waited for and then cut short, rather than the run being timed out and
            // asked afterwards what it had decided.
            while (!run.IsCompleted && state.Status != RunStatus.AwaitingOwner && !cts.IsCancellationRequested)
                await Task.Delay(100, CancellationToken.None);
            await cts.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
        SqliteConnection.ClearAllPools();

        var costs = RunArchive.TryOpen(plan.RunDbPath)!.Costs(state.RunId);
        var diag = Diagnose(plan, state, costs);

        // The lane's own row, first: without it the park below would be proving something else, and a
        // missing receipt must read as a missing receipt rather than as a run that simply finished.
        var lane = costs.SingleOrDefault(c => c.Category == SpendCategory.Lane);
        Assert.True(lane is not null,
            $"the analysis lane wrote no cost row — it never ran, errored, or reported no billed figure.\n{diag}");
        Assert.True(lane!.CostUsd == 0.0731m, $"the lane billed ${lane.CostUsd}, expected $0.0731.\n{diag}");
        Assert.Equal(8_000, lane.TokensCacheRead);

        // The delivery agent contributed nothing: this run's ONLY billed spend is a lane's and a
        // supervisor's, and it still reached the ceiling.
        Assert.True(state.PerRunCostUsd == 0m,
            $"the delivery agent was meant to report no figure, but billed ${state.PerRunCostUsd}.\n{diag}");
        Assert.DoesNotContain(costs, c => c.Category == SpendCategory.Agent);

        // The supervisor's row was written by another process and had no accrual of its own; the
        // engine took it in at the boundary.
        Assert.Contains(costs, c => c.Category == SpendCategory.Supervisor);
        Assert.True(Math.Abs(state.PerRunSideCostUsd - 0.0831m) < 0.0005m,
            $"side spend is ${state.PerRunSideCostUsd}, expected the lane's $0.0731 plus the supervisor's " +
            $"$0.0100 absorbed at the boundary.\n{diag}");

        Assert.True(state.Status == RunStatus.AwaitingOwner,
            $"the run did not park on its budget — status {state.Status}.\n{diag}");
        Assert.Equal(AwaitingOwnerReason.Budget, state.AwaitingOwnerReason);
        Assert.True(state.BilledWindowCostUsd >= plan.Limits.MaxRunCostUsd, diag);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>The rig shape both live tests use: one stage, one session, a trivial gate, no report
    /// commit. Only the agent and what the plan asks for on top of it differ between them.</summary>
    private static PlanConfig RigPlan(string name, string repo, AgentConfig agent)
    {
        var plan = new PlanConfig
        {
            Name = name,
            Repo = repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "L0", Title = "Lane rig", Sessions = 1 } },
            Agent = agent,
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;
        return plan;
    }

    /// <summary>Writes the row a <c>conductor watch</c> supervisor writes: into the run's database,
    /// from outside the engine, with NO accrual — the ledger built without an accrue callback is
    /// literally the shape <c>WatchCommand.RecordSupervisorSpend</c> constructs.</summary>
    private static void SeedSupervisorRowFromAnotherProcess(PlanConfig plan, string runId, decimal usd)
    {
        Directory.CreateDirectory(plan.StateDir);
        using (var store = new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan.Name, plan.Repo, "main", EngineStamp.Parse("0.4.0+ks52"));
            new RunSpendLedger(store, runId)
                .Record(new SpendReceipt(SpendCategory.Supervisor, usd, 40, 20, 0, 0, 1_500), 0, "supervisor hook");
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Everything a failed live assertion needs to be diagnosed without re-running it: what the
    /// run decided, every cost row it wrote, and the tail of its own log.</summary>
    private static string Diagnose(PlanConfig plan, RunState state, IReadOnlyList<ArchivedCost>? costs = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"run {state.RunId}: status={state.Status}/{state.AwaitingOwnerReason} " +
                      $"agent=${state.PerRunCostUsd} side=${state.PerRunSideCostUsd} window=${state.BilledWindowCostUsd}");
        try
        {
            costs ??= RunArchive.TryOpen(plan.RunDbPath)?.Costs(state.RunId) ?? [];
            sb.AppendLine(costs.Count == 0 ? "costs: NO ROWS" : "costs:");
            foreach (var c in costs) sb.AppendLine($"  s{c.SessionNumber} {c.Category} ${c.CostUsd} ({c.Tokens} tokens)");
        }
        catch (SqliteException ex) { sb.AppendLine("costs: unreadable — " + ex.Message); }
        catch (InvalidOperationException ex) { sb.AppendLine("costs: unreadable — " + ex.Message); }

        var log = Path.Combine(plan.StateDir, "conductor.log");
        try
        {
            if (File.Exists(log))
                sb.AppendLine("log tail:\n  " + string.Join("\n  ", File.ReadAllLines(log).TakeLast(40)));
        }
        catch (IOException ex) { sb.AppendLine("log: unreadable — " + ex.Message); }
        return sb.ToString();
    }

    private string WriteAgentScript(string name, string ndjson)
        => WriteScript(name, ["@echo off", .. EchoLines(ndjson), "exit /b 0"]);

    /// <summary>A fake agent that reports what it was billed for ONE caller and nothing for the other,
    /// told apart by the id the engine substitutes into its argv (<c>{sessionId}</c> — the lane's own id
    /// for a lane, the session's for a session). It is how a run can be given lane spend and no agent
    /// spend, which is the acceptance clause this rig exists to prove.</summary>
    private string WriteBillingByCallerScript(string name, string billsWhenArgIs)
        => WriteScript(name,
        [
            "@echo off",
            $"if /I \"%~1\"==\"{billsWhenArgIs}\" goto billed",
            .. EchoLines(UnbilledStream),
            "exit /b 0",
            ":billed",
            .. EchoLines(ClaudeStream),
            "exit /b 0",
        ]);

    private static IEnumerable<string> EchoLines(string ndjson)
        => ndjson.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => "echo " + l);

    private string WriteScript(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
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
