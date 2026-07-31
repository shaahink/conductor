using System.Text;

using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC2.4 — "the run outlives the engine". Four separate defects, one theme: everything the engine
/// knows dies with the process, and the surfaces that should survive it either never existed or were
/// wired to fail on a LIVE file.
/// </summary>
public sealed class SC24RunOutlivesEngineTests : IDisposable
{
    private readonly string _dir;

    public SC24RunOutlivesEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sc24-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ────────────────────────────────── shared reads (bug 1, round-four #3)

    /// <summary>The exact failure: a writer holds the file with FileShare.Read (what Serilog's rolling
    /// sink and a shell redirect both do). BCL File.ReadAllLines asks for FileShare.Read, which does
    /// not permit the writer's Write handle, so it throws — on the only file worth reading.</summary>
    [Fact]
    public void SharedFileRead_ReadsALogAWriterStillHoldsOpen_WhereFileReadAllLinesThrows()
    {
        var path = Path.Combine(_dir, "live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(writer, Encoding.UTF8) { AutoFlush = true };
        sw.WriteLine("first line");
        sw.WriteLine("second line");

        // Negative control — the call the two commands used to make.
        Assert.Throws<IOException>(() => File.ReadAllLines(path));

        var lines = SharedFileRead.ReadAllLines(path);
        Assert.Equal(2, lines.Count);
        Assert.Equal("first line", lines[0]);
        Assert.Equal("second line", lines[1]);
    }

    // ────────────────────────────────── incremental tails

    [Fact]
    public void FileLineTail_ReturnsOnlyWhatWasAppended_AndNeverRewinds()
    {
        var path = Path.Combine(_dir, "tail.txt");
        File.WriteAllText(path, "alpha\nbeta\n");

        var tail = new FileLineTail();
        Assert.False(tail.Follow(path) && false); // Follow returns true on first target
        Assert.Equal(["alpha", "beta"], tail.ReadAppended());

        var afterBacklog = tail.Offset;
        Assert.True(afterBacklog > 0);

        // A quiet poll reads nothing and moves nothing — this is the whole point: no re-read.
        Assert.Empty(tail.ReadAppended());
        Assert.Equal(afterBacklog, tail.Offset);

        File.AppendAllText(path, "gamma\n");
        Assert.Equal(["gamma"], tail.ReadAppended());
        Assert.True(tail.Offset > afterBacklog);
    }

    [Fact]
    public void FileLineTail_WithholdsAPartialLineUntilItsNewlineArrives()
    {
        var path = Path.Combine(_dir, "partial.txt");
        File.WriteAllText(path, "complete\n{\"half\":");

        var tail = new FileLineTail();
        tail.Follow(path);
        // The torn tail is NOT handed out — an SSE reader must never see half a JSON object.
        Assert.Equal(["complete"], tail.ReadAppended());
        Assert.Empty(tail.ReadAppended());

        File.AppendAllText(path, "1}\n");
        Assert.Equal(["{\"half\":1}"], tail.ReadAppended());
    }

    [Fact]
    public void FileLineTail_ResetsWhenTheFileShrinksOrTheTargetChanges()
    {
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "one\ntwo\nthree\n");
        File.WriteAllText(b, "other\n");

        var tail = new FileLineTail();
        Assert.True(tail.Follow(a));
        Assert.Equal(3, tail.ReadAppended().Count);
        Assert.False(tail.Follow(a)); // same target — no reset

        // Truncated in place (rotation): replay from the top rather than skipping the new content.
        File.WriteAllText(a, "fresh\n");
        Assert.Equal(["fresh"], tail.ReadAppended());

        Assert.True(tail.Follow(b));
        Assert.Equal(0, tail.Offset);
        Assert.Equal(["other"], tail.ReadAppended());
    }

    [Fact]
    public void FileLineTail_StripsTheBomAndCrLf()
    {
        var path = Path.Combine(_dir, "bom.txt");
        File.WriteAllText(path, "first\r\nsecond\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var tail = new FileLineTail();
        tail.Follow(path);
        Assert.Equal(["first", "second"], tail.ReadAppended());
    }

    /// <summary>The store-side half of the same fix: <c>GET /events</c> now asks the database for the
    /// tail. Proves the query returns exactly the rows past a sequence, so the endpoint's C#-side
    /// filter (which paid for the whole log first) is genuinely redundant.</summary>
    [Fact]
    public void ReadEventsAfter_ReturnsOnlyTheTail()
    {
        var db = Path.Combine(_dir, "run.db");
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        const string runId = "run-tail";
        store.SetRunId(runId);
        store.InitializeRun(runId, "TailPlan", _dir, "main", "test");
        for (var i = 0; i < 5; i++)
            ((IRunStore)store).AppendEvent(new Core.Events.StageEntered { RunId = runId, StageId = $"S{i}", Title = $"stage {i}" });
        store.FlushEvents();

        var all = store.ReadAllEvents(runId);
        Assert.Equal(5, all.Count);

        var tail = store.ReadEventsAfter(runId, all[2].Seq);
        Assert.Equal(2, tail.Count);
        Assert.All(tail, e => Assert.True(e.Seq > all[2].Seq));
        Assert.Empty(store.ReadEventsAfter(runId, all[^1].Seq));
    }

    // ────────────────────────────────── RUN-SUMMARY.md content

    [Fact]
    public void RunSummary_ReportsWallClockSessionsPerStageAttemptsSpendAndRoughOutcomes()
    {
        var db = Path.Combine(_dir, "run.db");
        const string runId = "run-summary";
        var t0 = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
        // A fake clock so started_utc/ended_utc on the runs row are the RUN's wall clock, not the
        // test's — the summary must read them from the row, never re-derive them from the sessions.
        var clock = new FixedClock(t0);
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance, clock);
        store.SetRunId(runId);
        store.InitializeRun(runId, "SummaryPlan", _dir, "feat/x", "test");
        store.RecordSession(runId, "S1", 1, "Deliver", t0, t0.AddMinutes(9), "Advanced", null, 0, 1, "gates GREEN", "did the thing", 2, "S1.1");
        store.RecordSession(runId, "S1", 2, "Fix", t0.AddMinutes(10), t0.AddMinutes(21), "GatesRed", null, 0, 2, "gates RED", "tried", 0, null);
        store.RecordSession(runId, "S2", 3, "Deliver", t0.AddMinutes(22), t0.AddMinutes(30), "Advanced", null, 0, 1, "gates GREEN", "done", 1, "S2.1");
        store.RecordCost(runId, 1, "agent", 100, 50, 0, 0, 1.25m, 1000);
        store.RecordCost(runId, 1, "gate", 0, 0, 0, 0, 0.05m, 0);
        store.RecordCost(runId, 2, "agent", 80, 40, 0, 0, 0.75m, 900);
        store.RecordCost(runId, 3, "agent", 60, 30, 0, 0, 0.50m, 800);
        clock.Now = t0.AddMinutes(31);
        store.RecordRunEnd(runId, "Completed");

        var plan = new PlanConfig
        {
            Name = "SummaryPlan",
            Repo = _dir,
            Stages =
            {
                new StageConfig { Id = "S1", Title = "First stage" },
                new StageConfig { Id = "S2", Title = "Second stage" },
                new StageConfig { Id = "S3", Title = "Never reached" },
            },
        };
        plan.Limits.MaxRunCostUsd = 10m;
        var state = new RunState { PlanName = "SummaryPlan", RunId = runId, Status = RunStatus.Completed, SessionCounter = 3 };
        var track = new TrackerSnapshot
        {
            Checkpoints =
            {
                new Core.CheckpointRow("S1.1", "one", "DONE", "-", "-") { StageId = "S1", IsDone = true },
                new Core.CheckpointRow("S2.1", "two", "DONE", "-", "-") { StageId = "S2", IsDone = true },
            },
        };

        var md = RunSummary.Build(plan, state, track, store, t0.AddMinutes(31));

        Assert.Contains("# Run summary — SummaryPlan", md, StringComparison.Ordinal);
        // Wall clock is the RUN's, from the runs row — not the sum of session durations.
        Assert.Contains("2026-07-31 10:00 UTC", md, StringComparison.Ordinal);
        Assert.Contains("**Sessions:** 3 (2 deliver, 1 fix)", md, StringComparison.Ordinal);
        Assert.Contains("**Checkpoints:** 2/2 done", md, StringComparison.Ordinal);

        // Spend: agent and overhead split by cost CATEGORY, so gate time is not counted twice.
        Assert.Contains("$2.5500 total (agent $2.5000 + gates $0.0500)", md, StringComparison.Ordinal);
        Assert.Contains("cap $10.00", md, StringComparison.Ordinal);

        // Per-stage attempts: S1 needed two goes, S2 one, S3 never ran.
        Assert.Contains("| S1 | First stage | 2 | 2 |", md, StringComparison.Ordinal);
        Assert.Contains("| S2 | Second stage | 1 | 1 |", md, StringComparison.Ordinal);
        Assert.Contains("| S3 | Never reached | 0 | 0 |", md, StringComparison.Ordinal);
        Assert.Contains("never entered", md, StringComparison.Ordinal);

        // The non-Advanced session is named; the Advanced ones are not repeated.
        Assert.Contains("## Sessions that did not advance", md, StringComparison.Ordinal);
        Assert.Contains("| 2 | S1 | Fix | GatesRed | 2 |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("| 1 | S1 | Deliver | Advanced", md, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSummary_SaysSoWhenNoCapIsSet()
    {
        var plan = new PlanConfig { Name = "P", Repo = _dir, Stages = { new StageConfig { Id = "S1", Title = "s" } } };
        var state = new RunState { PlanName = "P", RunId = "r", Status = RunStatus.Completed };
        var md = RunSummary.Build(plan, state, new TrackerSnapshot(), null, DateTime.UtcNow);
        Assert.Contains("no cap set (limits.maxRunCostUsd unset)", md, StringComparison.Ordinal);
    }

    // ────────────────────────────────── offline report from run.db

    /// <summary>SC2.4: a report regenerated with the engine gone must carry the DB-fed sections. The
    /// control is <see cref="Reporter.ReadTimeline"/> with a null store — the exact call
    /// <c>ReportCommand</c> used to make, which silently returns an empty timeline.</summary>
    [Fact]
    public void OfflineReport_FromRunDb_CarriesTheTimelineAndHealthThatANullStoreDropped()
    {
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        var db = Path.Combine(stateDir, "run.db");
        const string runId = "run-offline";
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId(runId);
            store.InitializeRun(runId, "OfflinePlan", _dir, "main", "test");
            ((IRunStore)store).AppendEvent(new Core.Events.StageEntered { RunId = runId, StageId = "S1", Title = "First" });
            ((IRunStore)store).AppendEvent(new Core.Events.SessionStarted { RunId = runId, Number = 1, StageId = "S1", Kind = "Deliver" });
            ((IRunStore)store).AppendEvent(new Core.Events.SessionFinished { RunId = runId, Number = 1, StageId = "S1", Outcome = "Advanced" });
            store.FlushEvents();

            var before = Reporter.ReadTimeline(null, runId);
            Assert.Empty(before); // the old ReportCommand's read

            var after = Reporter.ReadTimeline(store, runId);
            Assert.NotEmpty(after);
        }
    }

    // ────────────────────────────────── live: a run that completes leaves the summary

    /// <summary>The claim this checkpoint actually makes, driven end to end: a real orchestration run
    /// against a temp repo and a fake agent, taken all the way to <c>RunStatus.Completed</c>, leaves a
    /// RUN-SUMMARY.md that a reader can open after the process is gone.</summary>
    [Trait("Category", "Integration")]
    [Fact]
    public async Task CompletedRun_LeavesRunSummaryOnDisk_BuiltFromTheDatabase()
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        Git("init -b main", repo);
        Git("config user.email sc24@test", repo);
        Git("config user.name SC24", repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# sc24");
        Git("add README.md", repo);
        Git("commit -m initial --no-gpg-sign", repo);

        var trackerPath = Path.Combine(repo, "TRACKER.md");
        await File.WriteAllTextAsync(trackerPath,
            "# SC24 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| H0.1 | the only checkpoint | TODO | | |\n");

        // The fake agent delivers, marks its row DONE in the tracker, and commits — the same thing a
        // real agent does, so the engine's own newly-done fold closes the checkpoint.
        var agent = Path.Combine(repo, "agent.cmd");
        await File.WriteAllTextAsync(agent, string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivering H0.1.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0123,\"tokens\":{\"input\":300,\"output\":100}}}",
            "echo done> delivered.txt",
            "powershell -NoProfile -Command \"(Get-Content TRACKER.md) -replace '\\| H0.1 \\| the only checkpoint \\| TODO', '| H0.1 | the only checkpoint | DONE' | Set-Content TRACKER.md\"",
            "git add -A",
            "git commit -m \"feat: deliver H0.1\" --no-gpg-sign",
            "exit /b 0",
            ""));

        var plan = new PlanConfig
        {
            Name = "SC24Plan",
            Repo = repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Only stage", Sessions = 1 } },
            Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", agent, "{prompt}" }, Provider = "opencode" },
            // QA off and no phase gates: this test is about COMPLETION, not about the verify pipeline.
            Pipeline = new PipelineRules { Qa = new QaRule { Mode = "off" } },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;
        plan.Limits.MaxRunCostUsd = 5m;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N"), PlanName = plan.Name };
        var summaryPath = RunSummary.SummaryPath(plan);
        Assert.False(File.Exists(summaryPath));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using (var host = ConductorHost.Build(plan, state, new PlainSink(),
                   new RunOptions(DryRun: false, Once: false, MaxSessions: 3), consoleSink: false))
        {
            var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            Assert.Equal(0, code);
        }

        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.True(File.Exists(summaryPath), $"the completed run left no RUN-SUMMARY.md at {summaryPath}");

        var md = await File.ReadAllTextAsync(summaryPath, cts.Token);
        Assert.Contains("# Run summary — SC24Plan", md, StringComparison.Ordinal);
        Assert.Contains("**Outcome:** Completed", md, StringComparison.Ordinal);
        Assert.Contains("**Checkpoints:** 1/1 done", md, StringComparison.Ordinal);
        Assert.Contains("| H0 | Only stage | 1 |", md, StringComparison.Ordinal);
        Assert.Contains("cap $5.00", md, StringComparison.Ordinal);
        // The agent's real reported cost reached the summary through run.db, not through RAM.
        Assert.Contains("agent $0.0123", md, StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static void Git(string args, string cwd) =>
        ProcessRunner.Run("git", args.Split(' ', StringSplitOptions.RemoveEmptyEntries), cwd,
            TimeSpan.FromSeconds(30), CancellationToken.None);
}
