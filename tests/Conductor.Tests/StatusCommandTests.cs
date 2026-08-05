using System.Text;
using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Cli;

namespace Conductor.Tests;

public class StatusCommandTests
{
    // ── M5.6 truth gate: `conductor status` answers from run.db (the event log), never state.json ──

    /// <summary>The core M5.6 gate. A run.db is seeded with a completed run via the event log and NOTHING
    /// else — no state.json exists on disk. The report's verdict, counts and cost must all come back from
    /// the folded events, proving the database is the source of truth.</summary>
    [Fact]
    public void StatusReport_IsBuiltFromRunDb_WithNoStateJson()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1", Title = "first" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "Advanced", CostUsd = 0.42m, NewlyDone = ["D1"] },
            new StageConfirmed { StageId = "D1" },
            new RunFinished { Status = "Completed", Sessions = 1, CheckpointsDone = 1, CheckpointsTotal = 2 });

        Assert.False(File.Exists(Path.Combine(tmp.StateDir, "state.json")), "test must not write state.json");

        var report = tmp.BuildReport((_, _) => false);

        Assert.Equal("ok", report.Kind);
        Assert.Contains("Completed", report.Verdict);
        Assert.Equal(1, report.SessionCount);
        Assert.Equal(0.42m, report.TotalCostUsd);
        Assert.Equal("D1", report.RecentSessions.Single().Stage);
        Assert.Equal("Advanced", report.RecentSessions.Single().Outcome);
        // Stage/checkpoint state also reflects the DB: D1 confirmed via the StageConfirmed event.
        Assert.Equal("confirmed", report.Stages.Single(s => s.Id == "D1").State);
    }

    [Fact]
    public void StatusReport_NoRun_SaysSo()
    {
        using var tmp = new TempRunDb();
        // No InitializeRun / no events — there is a run.db file but no run recorded.
        var report = tmp.BuildReport((_, _) => false);
        Assert.Equal("norun", report.Kind);
    }

    [Fact]
    public void StatusReport_UnfinishedSession_IsInterrupted_WhenNoLiveProcess()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 3, StageId = "D1", Kind = "Deliver" });
        // No SessionFinished(#3) — a crash. No live process backs it.
        var report = tmp.BuildReport((_, _) => false);
        Assert.Equal("interrupted", report.Kind);
        Assert.Contains("#3", report.Verdict);
    }

    [Fact]
    public void StatusReport_UnfinishedSession_IsActive_WhenProcessAlive()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 3, StageId = "D1", Kind = "Deliver" });
        tmp.TrackLivePid(4242);
        var report = tmp.BuildReport((pid, _) => pid == 4242); // that pid is "alive"
        Assert.Equal("active", report.Kind);
    }

    // ── SC2.1: the verdict window is the engine working, not a crash ──

    /// <summary>
    /// SC2.1 regression. Between the agent process exiting and <c>SessionFinished</c> landing, the engine
    /// runs the entire gate battery — minutes, during which run.db holds an unmatched
    /// <c>SessionStarted</c> and not one live spawned pid (gate commands were never tracked in the pids
    /// table). Status called that healthy window "interrupted mid-session" and advised
    /// <c>conductor run</c> — the one command that starts a second engine on a live run. The engine's own
    /// lock file is the liveness the pids table never carried.
    /// </summary>
    [Fact]
    public void StatusReport_VerdictWindow_IsRunning_NotInterrupted_WhenEngineAlive()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 3, StageId = "D1", Kind = "Deliver" });
        tmp.TrackExitedPid(4242);   // the agent process is gone — the battery is what is running now
        tmp.WriteLiveEngineLock();  // ...inside an engine that is genuinely alive: this very process

        // No spawned pid is alive, and that answer is correct — the engine alone must carry the verdict.
        var report = tmp.BuildReport((_, _) => false);

        Assert.Equal("active", report.Kind);
        Assert.DoesNotContain("interrupted", report.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conductor run", report.Verdict, StringComparison.Ordinal);
        Assert.Contains("#3", report.Verdict);
    }

    /// <summary>
    /// The other half of SC2.1: the fix must not blanket-suppress the honest verdict. A lock file left
    /// behind by an engine that died holds a pid the OS may since have handed to something else — alive,
    /// but not ours. That is still an interrupted run.
    /// </summary>
    [Fact]
    public void StatusReport_RecycledEngineLockPid_IsStillInterrupted()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 3, StageId = "D1", Kind = "Deliver" });
        // A live pid — this process — recorded as having started years before it really did: exactly what
        // a recycled id looks like from run.db's side.
        tmp.WriteEngineLock(Environment.ProcessId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var report = tmp.BuildReport((_, _) => false);

        Assert.Equal("interrupted", report.Kind);
    }

    /// <summary>A lock naming a pid the OS no longer knows at all is a dead engine, not a live one.</summary>
    [Fact]
    public void StatusReport_DeadEngineLockPid_IsStillInterrupted()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 3, StageId = "D1", Kind = "Deliver" });
        tmp.WriteEngineLock(DeadPid(), DateTime.UtcNow);

        var report = tmp.BuildReport((_, _) => false);

        Assert.Equal("interrupted", report.Kind);
        Assert.Contains("conductor run", report.Verdict, StringComparison.Ordinal);
    }

    /// <summary>A pid that really has exited: spawned here, waited on, and reaped.</summary>
    private static int DeadPid()
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public void StatusReport_WhatHurt_SurfacesLastFailingGate()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new GateFinished { Name = "tests", Passed = false, Scope = "D1", DurationMs = 10 },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "GatesRed", CostUsd = 0.1m });
        var report = tmp.BuildReport((_, _) => false);
        Assert.NotNull(report.WhatHurt);
        Assert.Contains("tests", report.WhatHurt);
    }

    [Fact]
    public void StatusReport_WhatHurt_PrefersAttentionReason()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new GateFinished { Name = "tests", Passed = false, Scope = "D1", DurationMs = 10 },
            new AttentionRequested { Reason = "gate build failed 3 times consecutively" });
        var report = tmp.BuildReport((_, _) => false);
        Assert.StartsWith("gate build failed 3 times consecutively", report.WhatHurt, StringComparison.Ordinal);
        Assert.Equal("attention", report.Kind);
    }

    // ── SC2.2: the sticky failure field ages, and clears when the condition clears ──

    /// <summary>SC2.2. "what hurt" was a sentence with no age: a failure from four seconds ago and one
    /// from four hours ago read identically, so an operator could not tell a live problem from an old
    /// one without opening the log.</summary>
    [Fact]
    public void StatusReport_WhatHurt_CarriesItsAge()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new GateFinished { Name = "tests", Passed = false, Scope = "D1", DurationMs = 10 },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "GatesRed", CostUsd = 0.1m });
        var report = tmp.BuildReport((_, _) => false);
        Assert.NotNull(report.WhatHurt);
        Assert.Contains(" ago, ", report.WhatHurt, StringComparison.Ordinal);
        Assert.Contains("Z]", report.WhatHurt, StringComparison.Ordinal);
    }

    /// <summary>SC2.2. The old rule was "newest failure anywhere in the log wins", so a gate that failed
    /// once and has passed on every run since stayed the headline complaint for the rest of the run.</summary>
    [Fact]
    public void StatusReport_WhatHurt_Clears_WhenTheSameGatePassesLater()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new GateFinished { Name = "tests", Passed = false, Scope = "D1", DurationMs = 10 },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "GatesRed", CostUsd = 0.1m },
            new SessionStarted { Number = 2, StageId = "D1", Kind = "Fix" },
            new GateFinished { Name = "tests", Passed = true, Scope = "D1", DurationMs = 10 },
            new SessionFinished { Number = 2, StageId = "D1", Outcome = "Advanced", CostUsd = 0.1m, NewlyDone = ["D1"] });
        var report = tmp.BuildReport((_, _) => false);
        Assert.Null(report.WhatHurt);
    }

    /// <summary>A gate that is still failing is still the complaint — the clearing rule must not blanket
    /// every old failure just because some other gate went green afterwards.</summary>
    [Fact]
    public void StatusReport_WhatHurt_Survives_WhenADifferentGatePasses()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new GateFinished { Name = "tests", Passed = false, Scope = "D1", DurationMs = 10 },
            new GateFinished { Name = "build", Passed = true, Scope = "D1", DurationMs = 10 },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "GatesRed", CostUsd = 0.1m });
        var report = tmp.BuildReport((_, _) => false);
        Assert.NotNull(report.WhatHurt);
        Assert.Contains("tests", report.WhatHurt, StringComparison.Ordinal);
    }

    /// <summary>A confirmed stage is a green full battery. Nothing older than it is still hurting — and a
    /// park the operator has since cleared is exactly the "sticky for hours" complaint.</summary>
    [Fact]
    public void StatusReport_WhatHurt_Clears_AfterTheStageConfirms()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new AttentionRequested { Reason = "stage D1 used all 2 attempts" },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "Advanced", CostUsd = 0.1m, NewlyDone = ["D1"] },
            new StageConfirmed { StageId = "D1" });
        var report = tmp.BuildReport((_, _) => false);
        Assert.Null(report.WhatHurt);
    }

    /// <summary>SC2.2. The park says "inspect and <c>conductor resume</c>". Clearing it only on
    /// <see cref="StageConfirmed"/> meant that once the operator had done exactly that, status kept
    /// repeating the instruction for the whole session that followed — a run visibly working, for
    /// thirty-eight minutes, under a banner saying it was stopped and needed a human.</summary>
    [Fact]
    public void StatusReport_WhatHurt_Clears_OnceASessionRunsAfterThePark()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "AgentError", CostUsd = 0m },
            new AttentionRequested { Reason = "stage D1 used all 2 attempts without completing" },
            new SessionStarted { Number = 2, StageId = "D1", Kind = "Fix" });
        var report = tmp.BuildReport((_, _) => false);
        Assert.Null(report.WhatHurt);
    }

    /// <summary>The other half of the same rule: a park with no session after it has not been answered,
    /// so it is still the complaint. Only work moving clears it — not the operator merely looking.</summary>
    [Fact]
    public void StatusReport_WhatHurt_Survives_WhileTheRunIsStillParked()
    {
        using var tmp = new TempRunDb();
        tmp.Seed(
            new RunStarted { Plan = TempRunDb.Plan, Repo = "." },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "AgentError", CostUsd = 0m },
            new AttentionRequested { Reason = "stage D1 used all 2 attempts without completing" });
        var report = tmp.BuildReport((_, _) => false);
        Assert.StartsWith("stage D1 used all 2 attempts without completing", report.WhatHurt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDefaultsWork()
    {
        var s = new StatusCommand.Settings();
        Assert.Null(s.Since);
        Assert.False(s.Deep);
        Assert.Null(s.Plan);
    }

    [Fact]
    public void CliPromptContainsKeyContext()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        var track = TrackerParser.Parse(SampleTracker);
        var logTail = "2026-07-09 12:00 [INFO] gate build: OK\n2026-07-09 12:01 [INFO] gate tests: OK";
        var gitSummary = "branch: feat/era-v3\nHEAD: abc1234\nrecent commits:\n  abc1234 feat(era3): D1 status";

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, logTail, gitSummary, 1, 2, null);

        Assert.Contains("Conductor-Era3", prompt);
        Assert.Contains("Idle", prompt);
        Assert.Contains("gate build: OK", prompt);
        Assert.Contains("feat/era-v3", prompt);
        Assert.Contains("abc1234", prompt);
        Assert.Contains("D1", prompt);
        Assert.Contains("D2", prompt);
    }

    [Fact]
    public void CliPromptIncludesSinceWhenSet()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        var track = TrackerParser.Parse(SampleTracker);
        var since = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, since);

        Assert.Contains("Since:", prompt);
        Assert.Contains("2026", prompt);
    }

    [Fact]
    public void CliPromptIncludesPendingFixWhenPresent()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.PendingFix = new PendingFix { FromSession = 5, GateFailures = "tests", ProgressSummary = "x" };
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, null);

        Assert.Contains("Pending fix", prompt);
        Assert.Contains("#5", prompt);
        Assert.Contains("tests", prompt);
    }

    [Fact]
    public void CliPromptIncludesPendingPhaseGateWhenPresent()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.PendingPhaseGate = new PendingPhaseGate { StageId = "D1", StageStartHead = "abc" };
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, null);

        Assert.Contains("Pending phase gate", prompt);
        Assert.Contains("D1", prompt);
    }

    [Fact]
    public void CliPromptIncludesHistory()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.History.Add(new SessionRecord
        {
            Number = 1,
            Stage = "D1",
            Kind = SessionKind.Deliver,
            Outcome = SessionOutcome.Advanced,
            NewlyDone = new List<string> { "D1" },
            NewCommits = new List<string> { "abc123" },
            GateSummary = "build:OK · tests:OK",
        });
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 1, 2, null);

        Assert.Contains("Recent sessions", prompt);
        Assert.Contains("#1", prompt);
        Assert.Contains("Deliver", prompt);
        Assert.Contains("Advanced", prompt);
        Assert.Contains("commits: 1", prompt);
    }

    [Fact]
    public void CliPromptIncludesAttentionReason()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.AttentionReason = "gate build failed 3 times consecutively";
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, null);

        Assert.Contains("gate build failed 3 times consecutively", prompt);
    }

    [Fact]
    public void StatusAgentConfigModelDefaultIsNull()
    {
        var json = "{}";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Model);
    }

    [Fact]
    public void StatusAgentConfigModelDeserializes()
    {
        var json = """{ "model": "deepseek/deepseek-chat" }""";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal("deepseek/deepseek-chat", cfg.Model);
    }

    [Fact]
    public void StatusAgentConfigMaxPerHourDefaultIs12()
    {
        var json = "{}";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(12, cfg.MaxPerHour);
    }

    [Fact]
    public void StatusAgentConfigMaxPerHourDeserializes()
    {
        var json = """{ "maxPerHour": 5 }""";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(5, cfg.MaxPerHour);
    }

    [Fact]
    public void BuildPromptStillWorksForDashboard()
    {
        var snap = new DashboardSnapshot
        {
            PlanName = "Era3",
            Status = "Running",
            StageId = "D1",
            StageTitle = "status command",
            SessionNumber = 1,
            SessionKind = "Deliver",
            DoneCount = 0,
            TotalCount = 14,
            CurrentCheckpoint = "D1",
            CurrentCheckpointTitle = "conductor status",
            GateSummary = "build:OK · tests:OK",
            StageOverview = new[] { ("D1", 0, 1, "active"), ("D2", 0, 1, "todo") },
        };
        var prompt = StatusAgent.BuildPrompt(snap, "branch: feat/era-v3\n  abc1234 feat(era3): D1",
            new[] { "bash: dotnet build" }, new[] { "thinking about status command" });

        Assert.Contains("read-only status reporter", prompt);
        Assert.Contains("Era3", prompt);
        Assert.Contains("D1", prompt);
        Assert.Contains("status command", prompt);
        Assert.Contains("build:OK", prompt);
    }

    [Fact]
    public void PlanConfigLoadsNewStatusFields()
    {
        var json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "statusAgent": { "enabled": true, "model": "deepseek/deepseek-flash", "maxPerHour": 6 }
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;

        Assert.NotNull(cfg.StatusAgent);
        Assert.True(cfg.StatusAgent.Enabled);
        Assert.Equal("deepseek/deepseek-flash", cfg.StatusAgent.Model);
        Assert.Equal(6, cfg.StatusAgent.MaxPerHour);
    }

    private static PlanConfig CreateSamplePlan()
    {
        return new PlanConfig
        {
            Name = "Conductor-Era3",
            Repo = "C:/Code/conductor-baton",
            Tracker = "TRACKER.md",
            Stages = new List<StageConfig>
            {
                new() { Id = "D1", Title = "conductor status", Sessions = 1 },
                new() { Id = "D2", Title = "conductor gate", Sessions = 1 },
            },
            Agent = new AgentConfig { Command = "opencode", Args = new List<string> { "run", "{prompt}" } },
        };
    }

    private static RunState CreateSampleState()
    {
        return new RunState
        {
            PlanName = "Conductor-Era3",
            Status = RunStatus.Idle,
            CurrentStage = "D1",
            SessionCounter = 0,
        };
    }

    private const string SampleTracker = """
        # Conductor-Era3 — Tracker

        ## Handoff
        last: Plan created. All stages TODO.
        stage: D1

        ## Checkpoints

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | D1 | conductor status | DONE | | |
        | D2 | conductor gate | TODO | | |
        """;

    /// <summary>A throwaway <c>run.db</c> on disk seeded through the real event sink and read back by a
    /// fresh store — exactly what <c>conductor status</c> does against a run.db a prior <c>conductor run</c>
    /// left behind. No state.json is ever written, which is the point of the M5.6 gate.</summary>
    private sealed class TempRunDb : IDisposable
    {
        public const string Plan = "status-db-test";
        private const string RunId = "run-status-test";

        private readonly string _dir;
        private readonly string _dbPath;
        public string StateDir { get; }
        public PlanConfig PlanConfig { get; }

        public TempRunDb()
        {
            _dir = Path.Combine(Path.GetTempPath(), "conductor-status-" + Guid.NewGuid().ToString("N"));
            StateDir = Path.Combine(_dir, ".conductor");
            Directory.CreateDirectory(StateDir);
            _dbPath = Path.Combine(StateDir, "run.db");
            PlanConfig = new PlanConfig
            {
                Name = Plan,
                Repo = _dir,
                Tracker = "TRACKER.md", // intentionally absent — status must not depend on it
                Stages =
                {
                    new StageConfig { Id = "D1", Title = "first", Sessions = 1 },
                    new StageConfig { Id = "D2", Title = "second", Sessions = 1 },
                },
                Agent = new AgentConfig { Command = "opencode", Args = new List<string> { "run", "{prompt}" } },
            };
            using var _ = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance); // materialise the file
        }

        public void Seed(params ConductorEvent[] events)
        {
            using (var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
            {
                store.SetRunId(RunId);
                store.InitializeRun(RunId, Plan, _dir, null, null);
                foreach (var e in events) store.Emit(e);
            } // dispose flushes the async event drain

            // The event drain is asynchronous; under a saturated full-suite run its flush-on-dispose can
            // lag. Confirm every event has landed through a fresh store before the test reads the db —
            // exactly the read-after-write `conductor status` performs against a run.db a prior run left.
            WaitForEventCount(events.Length);
        }

        private void WaitForEventCount(int expected)
        {
            if (expected == 0) return;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
                if (store.ReadAllEvents(RunId).Count >= expected) return;
                System.Threading.Thread.Sleep(25);
            }
            throw new TimeoutException($"seeded events did not persist to run.db (expected {expected}).");
        }

        public void TrackLivePid(int pid)
        {
            using var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
            store.TrackPid(pid, RunId, "agent:deliver", "D1", 3, DateTime.UtcNow);
        }

        /// <summary>An agent process that has already exited — the state of every spawned pid while the
        /// engine works through the gate battery.</summary>
        public void TrackExitedPid(int pid)
        {
            using var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
            store.TrackPid(pid, RunId, "agent:deliver", "D1", 3, DateTime.UtcNow.AddMinutes(-5));
            store.MarkPidExited(pid, 0);
        }

        /// <summary>The lock a live engine holds, written by the engine's own code path and naming this
        /// test process — real pid, real start time, so liveness is answered by the OS with no stub in
        /// the way.</summary>
        public void WriteLiveEngineLock() => EngineLock.Write(StateDir);

        public void WriteEngineLock(int pid, DateTime startedUtc) =>
            File.WriteAllText(EngineLock.PathFor(StateDir),
                pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + startedUtc.ToString("O"));

        public StatusReport BuildReport(Func<int, DateTime, bool> isAlive)
        {
            using var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
            return StatusReportBuilder.Build(PlanConfig, store, isAlive);
        }

        public void Dispose()
        {
            try { TestTemp.DeleteTree(_dir); } catch (IOException) { /* best effort */ }
        }
    }
}
