using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W3.1 truth gates — the autonomy rails, proven to fire.
///
/// Three defects, all confirmed against U-series artifacts: the hard timeout lived inside the poll
/// loop (bug #8 — a 90-minute limit fired at 337 minutes), the stall detector counted the agent's
/// own pid as "a bg process is alive" (so zero <c>stall:</c> lines exist in any engine log ever
/// written), and neither rail notified anyone. The unit gates drive the watchdog against injected
/// clocks; the live gates run the real engine with a real fake-agent child.
/// </summary>
public sealed class W3WatchdogTests
{
    private static SessionWatchdog Rig(
        TimeSpan hardTimeout, TimeSpan stall, TimeSpan grace,
        Func<WatchdogSignals> sample, Func<TimeSpan> monotonic, Func<DateTime> wall,
        Action<WatchdogAction, string>? onAction = null) =>
        new(hardTimeout, stall, grace, sample, onAction ?? ((_, _) => { }),
            tickInterval: TimeSpan.FromMilliseconds(20), monotonic: monotonic, wallClock: wall);

    private static WatchdogSignals Busy(DateTime now) => new(now, now, AnyBgProcessAlive: false);

    // ---------------------------------------------------------------- bug #8: an INDEPENDENT timer

    [Fact]
    public void HardTimeout_FiresWhileTheCallerIsBlocked()
    {
        // The bug #8 shape: the "poll loop" (this thread) is wedged and cannot evaluate anything.
        // The rail must still fire, on its own thread, on time.
        using var tripped = new ManualResetEventSlim(false);
        var trippedAt = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        using var watchdog = new SessionWatchdog(
            hardTimeout: TimeSpan.FromMilliseconds(300),
            stallThreshold: TimeSpan.FromMinutes(30),
            stallGrace: TimeSpan.FromMinutes(5),
            sample: () => Busy(DateTime.UtcNow),
            onAction: (action, _) =>
            {
                if (action != WatchdogAction.Timeout) return;
                trippedAt = sw.Elapsed;
                tripped.Set();
            },
            tickInterval: TimeSpan.FromMilliseconds(20));
        watchdog.Start();

        // Block hard — no awaits, no yields, nothing the loop could have serviced.
        Thread.Sleep(1500);

        Assert.True(tripped.IsSet, "the hard timeout never fired while the caller was blocked");
        Assert.True(watchdog.TimedOut);
        Assert.True(trippedAt < TimeSpan.FromMilliseconds(900),
            $"timeout fired {trippedAt.TotalMilliseconds:0}ms in — it waited for the blocked caller");
    }

    [Fact]
    public void HardTimeout_TripsExactlyOnce()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        using var w = Rig(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5),
            () => Busy(now), () => mono, () => now);

        mono += TimeSpan.FromMinutes(91); now = now.AddMinutes(91);
        Assert.Equal(WatchdogAction.Timeout, w.Tick().Action);

        mono += TimeSpan.FromMinutes(10); now = now.AddMinutes(10);
        Assert.Equal(WatchdogAction.None, w.Tick().Action);
        Assert.True(w.TimedOut);
    }

    // ---------------------------------------------------------------- sleep / hibernate

    [Fact]
    public void MachineSleep_IsExcludedFromTheTimeoutBudget()
    {
        // Wall clock leaps four hours; the monotonic clock does not. That is a suspend, not a
        // 337-minute session — the timeout must not fire, and the excluded time is accounted for.
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        using var w = Rig(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5),
            () => Busy(now), () => mono, () => now);

        mono += TimeSpan.FromSeconds(1); now = now.AddHours(4);
        var (action, message) = w.Tick();
        Assert.Equal(WatchdogAction.Diagnostic, action);
        Assert.Contains("clock jump", message, StringComparison.Ordinal);
        Assert.False(w.TimedOut);
        Assert.True(w.ExcludedSuspendTime >= TimeSpan.FromHours(3.9));
        Assert.True(w.Elapsed < TimeSpan.FromMinutes(1), $"elapsed {w.Elapsed} counted the sleep");

        // …and the budget still runs out on real, awake time.
        for (var i = 0; i < 91; i++) { mono += TimeSpan.FromMinutes(1); now = now.AddMinutes(1); w.Tick(); }
        Assert.True(w.TimedOut);
    }

    [Fact]
    public void MachineSleep_IsExcludedFromTheStallBudget()
    {
        // The agent went quiet 30 seconds before the machine slept for four hours. On wake, the
        // naive reading is "quiet for 4h" → a bogus stall kill of a perfectly healthy session.
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        var lastActivity = now.AddSeconds(-30);
        using var w = Rig(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(3),
            () => new WatchdogSignals(lastActivity, lastActivity, AnyBgProcessAlive: false),
            () => mono, () => now);

        mono += TimeSpan.FromSeconds(1); now = now.AddHours(4);
        Assert.Equal(WatchdogAction.Diagnostic, w.Tick().Action);   // the jump itself

        mono += TimeSpan.FromSeconds(1); now = now.AddSeconds(1);
        Assert.Equal(WatchdogAction.None, w.Tick().Action);          // …and no stall from it
        Assert.False(w.Stalled);
    }

    [Fact]
    public void BackwardsClockStep_IsReportedAndIgnored()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        using var w = Rig(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(3),
            () => Busy(now), () => mono, () => now);

        mono += TimeSpan.FromSeconds(1); now = now.AddHours(-2);
        var (action, message) = w.Tick();
        Assert.Equal(WatchdogAction.Diagnostic, action);
        Assert.Contains("BACKWARDS", message, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, w.ExcludedSuspendTime);
        Assert.False(w.TimedOut);
    }

    // ---------------------------------------------------------------- the stall rail

    [Fact]
    public void StallRail_RunsGraceThenKills()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        var quietSince = now;
        using var w = Rig(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(3),
            () => new WatchdogSignals(quietSince, quietSince, AnyBgProcessAlive: false),
            () => mono, () => now);

        void Advance(int minutes) { mono += TimeSpan.FromMinutes(minutes); now = now.AddMinutes(minutes); }

        Advance(1);
        Assert.Equal(WatchdogAction.None, w.Tick().Action);
        Advance(12);
        Assert.Equal(WatchdogAction.StallGraceStarted, w.Tick().Action);
        Advance(1);
        Assert.Equal(WatchdogAction.None, w.Tick().Action);   // grace already reported — no log spam
        Advance(3);
        var (action, message) = w.Tick();
        Assert.Equal(WatchdogAction.StallKill, action);
        Assert.Contains("killing session", message, StringComparison.Ordinal);
        Assert.True(w.Stalled);
    }

    [Fact]
    public void StallRail_HeldOffByALiveBgProcess()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        var quietSince = now;
        using var w = Rig(TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(3),
            () => new WatchdogSignals(quietSince, quietSince, AnyBgProcessAlive: true),
            () => mono, () => now);

        mono += TimeSpan.FromMinutes(30); now = now.AddMinutes(30);
        Assert.Equal(WatchdogAction.None, w.Tick().Action);
        Assert.False(w.Stalled);
    }

    // ---------------------------------------------------------------- why the rail was dead code

    [Fact]
    public void BgLiveness_CountsOnlyBgPurposes_NotTheAgentOrTheFace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w31-bg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".conductor"));
        try
        {
            using var db = new SqliteRunStore(Path.Combine(dir, ".conductor", "run.db"),
                NullLogger<SqliteRunStore>.Instance);
            const string runId = "w31-bg";
            db.InitializeRun(runId, "p", dir, "main", Conductor.Core.EngineStamp.Parse("1.0.0"));

            // This process is unquestionably alive. Tracked as the agent and the Face — exactly the
            // rows every real session writes — it must NOT read as bg liveness.
            var self = Environment.ProcessId;
            db.TrackPid(self, runId, "agent:stage:1:session#1", "H0", 1, DateTime.UtcNow);
            Assert.False(StallDetector.AnyBgProcessAlive(db, runId));

            db.MarkPidExited(self, 0);
            db.TrackPid(self, runId, "face:tui", null, null, DateTime.UtcNow);
            Assert.False(StallDetector.AnyBgProcessAlive(db, runId));

            // The same pid, deliberately backgrounded, is the signal the detector was built for.
            db.MarkPidExited(self, 0);
            db.TrackPid(self, runId, "bg:backtest", "H0", 1, DateTime.UtcNow);
            Assert.True(StallDetector.AnyBgProcessAlive(db, runId));
        }
        finally { try { TestTemp.DeleteTree(dir); } catch (IOException) { } }
    }

    [Fact]
    public void BgLiveness_ReapsDeadRowsOfEveryPurpose()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w31-reap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".conductor"));
        try
        {
            using var db = new SqliteRunStore(Path.Combine(dir, ".conductor", "run.db"),
                NullLogger<SqliteRunStore>.Instance);
            const string runId = "w31-reap";
            db.InitializeRun(runId, "p", dir, "main", Conductor.Core.EngineStamp.Parse("1.0.0"));
            // A pid that cannot exist (pid 0 is never a user process on Windows/Linux).
            db.TrackPid(999_999_9, runId, "agent:stage:1:session#1", null, null, DateTime.UtcNow);

            Assert.False(StallDetector.AnyBgProcessAlive(db, runId));
            // The purpose filter changed what counts as liveness, not what gets cleaned up.
            Assert.Empty(db.GetOrphanPids(runId));
        }
        finally { try { TestTemp.DeleteTree(dir); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- live gates (real engine)

    private static string Tracker(params string[] rows) =>
        "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
        + string.Join("\n", rows) + "\n";

    private static async Task<PlanConfig> ScaffoldAsync(string repo, string agentBody, Action<PlanConfig> tweak)
    {
        Directory.CreateDirectory(repo);
        ProcResult Git(string args) => ProcessRunner.Run("git",
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Git("init -b main");
        Git("config user.email w31@test");
        Git("config user.name W31");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
        Git("add README.md");
        Git("commit -m init --no-gpg-sign");
        await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"), Tracker("| H0.1 | the item | TODO | | |"));

        var agentScript = Path.Combine(repo, "fake-agent.cmd");
        await File.WriteAllTextAsync(agentScript, agentBody);

        var planPath = Path.Combine(repo, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "w31-live",
            Repo = repo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "H0", Title = "Hangs", Sessions = 1 }],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
            Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            // The rail must reach a human. Before W3.1 only a NeedsHuman park ever notified, so a
            // hung session was silent — this notify command is the observable end of that path.
            Notify = new NotifyConfig
            {
                Command = "cmd.exe",
                Args = ["/c", "echo", "{message}", ">>", Path.Combine(repo, "notify.txt")],
            },
        };
        seed.Report.Commit = false;
        tweak(seed);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return PlanConfig.Load(planPath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveRun_SilentAgent_TripsTheStallRail_AndNotifies()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w31-stall-{Guid.NewGuid():N}");
        try
        {
            // One line of output, then silence for two minutes — the shape no engine log has ever
            // caught, because the agent's own live pid always read as "bg work in progress".
            var plan = await ScaffoldAsync(repo, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"thinking...\"}}",
                "ping -n 120 127.0.0.1 >nul",
                "exit /b 0",
                ""),
                p =>
                {
                    p.Limits.StallSeconds = 3;
                    p.Limits.StallGraceSeconds = 2;
                    p.Limits.SessionTimeoutSeconds = 300;
                });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
            var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(0, code);

            var rec = Assert.Single(state.History);
            Assert.Equal(SessionOutcome.Stalled, rec.Outcome);
            Assert.True((rec.EndedUtc - rec.StartedUtc)!.Value < TimeSpan.FromSeconds(90),
                "the stall rail let a silent agent run to its natural end");

            var log = await File.ReadAllTextAsync(Path.Combine(repo, ".conductor", "conductor.log"));
            Assert.Contains("soft-kill grace window started", log, StringComparison.Ordinal);
            Assert.Contains("stall: grace window expired", log, StringComparison.Ordinal);

            // …and it reached the operator, not just the run log.
            var notifyPath = Path.Combine(repo, "notify.txt");
            Assert.True(File.Exists(notifyPath), "a stalled session notified nobody");
            var notified = await File.ReadAllTextAsync(notifyPath);
            Assert.Contains("stalled", notified, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveRun_ChattyHang_IsKilledOnTimeByTheHardTimeout()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w31-timeout-{Guid.NewGuid():N}");
        try
        {
            // Bug #8's real shape: the agent keeps emitting, so the stall rail stays quiet — only
            // the hard timeout can end it. Session #12 ran 337 minutes against a 90-minute limit.
            var plan = await ScaffoldAsync(repo, string.Join("\r\n",
                "@echo off",
                ":loop",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"still going...\"}}",
                "ping -n 2 127.0.0.1 >nul",
                "goto loop",
                ""),
                p =>
                {
                    p.Limits.SessionTimeoutSeconds = 6;
                    p.Limits.StallSeconds = 120;
                    p.Limits.StallGraceSeconds = 30;
                });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
            var started = DateTime.UtcNow;
            var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(0, code);

            var rec = Assert.Single(state.History);
            Assert.Equal(SessionOutcome.TimedOut, rec.Outcome);
            var ranFor = (rec.EndedUtc - rec.StartedUtc)!.Value;
            Assert.True(ranFor < TimeSpan.FromSeconds(45),
                $"the 6s timeout killed the session {ranFor.TotalSeconds:0}s in — that is bug #8 again");
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(120));

            var log = await File.ReadAllTextAsync(Path.Combine(repo, ".conductor", "conductor.log"));
            Assert.Contains("timeout: session exceeded", log, StringComparison.Ordinal);
            var notified = await File.ReadAllTextAsync(Path.Combine(repo, "notify.txt"));
            Assert.Contains("hard timeout", notified, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
    }
}
