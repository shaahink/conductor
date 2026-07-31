using System.Diagnostics;
using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Core.Orchestration;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC4.1 — the battery settles before it judges, retries a failed required gate once, and says how
/// long a failure took against how long that gate took when it last passed.
///
/// Every assertion here is measured against a real process, a real run.db or a real battery. The
/// defect this stage exists for (devcontext #12) was a verdict argued from a log line that did not
/// carry the number it was argued from, against a tree the session was still writing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC4_1SettleAndRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sc41-{Guid.NewGuid():N}");
    private readonly List<Process> _spawned = new();

    public SC4_1SettleAndRetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            p.Dispose();
        }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ────────────────────────────────────────────────────────── settle: real processes, real run.db

    private SqliteRunStore NewStore(string name) =>
        new(Path.Combine(_dir, $"{name}.db"), NullLogger<SqliteRunStore>.Instance);

    /// <summary>A process nobody's job object owns, so its lifetime is exactly what the test says.</summary>
    private Process Sleeper(int pings)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n {pings} 127.0.0.1 > NUL")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _spawned.Add(p);
        return p;
    }

    [Fact]
    public async Task Settle_WaitsForALiveBgChildAndReturnsOnlyOnceItHasActuallyExited()
    {
        using var store = NewStore("settle-waits");
        var child = Sleeper(4); // ~3s
        store.TrackPid(child.Id, "run-1", "bg:tests", "S1", 1, DateTime.UtcNow);

        var lines = new List<string>();
        var outcome = await BatterySettler.SettleAsync(store, "run-1", 1, TimeSpan.FromSeconds(60),
            (m, o) => lines.Add($"{o}|{m}"), poll: TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, outcome.Watched);
        Assert.Equal(0, outcome.StillAlive);
        Assert.True(outcome.Waited >= TimeSpan.FromSeconds(1), $"settle returned after only {outcome.Waited}");
        Assert.True(child.HasExited, "the settle returned while its child was still running");
        Assert.Contains(lines, l => l.Contains("holding gates", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("pass|", StringComparison.Ordinal) && l.Contains("exited after", StringComparison.Ordinal));
        // The row it was waiting on is closed, not left as phantom liveness for `bg status`.
        Assert.NotNull(store.GetAllPids("run-1").Single().ExitedUtc);
    }

    [Fact]
    public async Task Settle_GivesUpAtTheCapAndSaysSoRatherThanBlockingTheRunForever()
    {
        using var store = NewStore("settle-cap");
        var child = Sleeper(60); // outlives the cap by a wide margin
        store.TrackPid(child.Id, "run-1", "bg:dev-server", "S1", 1, DateTime.UtcNow);

        var lines = new List<string>();
        var outcome = await BatterySettler.SettleAsync(store, "run-1", 1, TimeSpan.FromSeconds(1),
            (m, o) => lines.Add($"{o}|{m}"), poll: TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, outcome.StillAlive);
        Assert.True(outcome.Waited < TimeSpan.FromSeconds(20), $"the cap did not hold: waited {outcome.Waited}");
        Assert.False(child.HasExited, "the settle must delay the verdict, never kill the child");
        var warn = Assert.Single(lines, l => l.StartsWith("warn|", StringComparison.Ordinal));
        Assert.Contains("dev-server", warn, StringComparison.Ordinal);
        Assert.Contains("starting gates anyway", warn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settle_IsFreeWhenNothingIsRunningAndOptOutIsHonoured()
    {
        using var store = NewStore("settle-free");
        Assert.Same(BatterySettleOutcome.Nothing,
            await BatterySettler.SettleAsync(store, "run-1", 1, TimeSpan.FromSeconds(60)));

        var child = Sleeper(60);
        store.TrackPid(child.Id, "run-1", "bg:tests", "S1", 1, DateTime.UtcNow);
        // limits.batterySettleSeconds = 0 — the whole wait is off, not merely short.
        Assert.Same(BatterySettleOutcome.Nothing,
            await BatterySettler.SettleAsync(store, "run-1", 1, TimeSpan.Zero));
    }

    [Fact]
    public void Settle_WatchesThisSessionsChildrenAndUnattributedOnes_NotAnEarlierSessionsServer()
    {
        using var store = NewStore("settle-scope");
        var mine = Sleeper(60);
        var unattributed = Sleeper(60);
        var older = Sleeper(60);
        var agent = Sleeper(60);
        store.TrackPid(mine.Id, "run-1", "bg:my-tests", "S1", 4, DateTime.UtcNow);
        store.TrackPid(unattributed.Id, "run-1", "bg:mcp-started", null, null, DateTime.UtcNow);
        store.TrackPid(older.Id, "run-1", "bg:someones-server", "S1", 2, DateTime.UtcNow);
        // Not a bg child at all: the agent process and the Face are what made the stall rail dead code.
        store.TrackPid(agent.Id, "run-1", "agent:S1:4", "S1", 4, DateTime.UtcNow);

        var watched = BatterySettler.LiveChildren(store, "run-1", 4).Select(p => p.Pid).ToList();

        Assert.Contains(mine.Id, watched);
        Assert.Contains(unattributed.Id, watched);
        Assert.DoesNotContain(older.Id, watched);
        Assert.DoesNotContain(agent.Id, watched);
    }

    // ────────────────────────────────────────────────────────── retry: real gates, real exit codes

    private PlanConfig GatePlan(params GateConfig[] gates) => new() { Repo = _dir, Gates = gates.ToList() };

    /// <summary>A gate that fails its first invocation and passes every one after it — the flake
    /// devcontext #12 paid a fix session for.</summary>
    private GateConfig FlakyGate(string name, bool optional = false)
    {
        var counter = Path.Combine(_dir, $"{name}-runs.txt");
        return new GateConfig
        {
            Name = name,
            Optional = optional,
            TimeoutMinutes = 1,
            Command = $"$f = '{counter}'; $n = 0; if (Test-Path $f) {{ $n = [int](Get-Content $f) }}; " +
                      "$n = $n + 1; Set-Content -Path $f -Value $n; " +
                      "if ($n -lt 2) { Write-Output 'flake: first run'; exit 1 }; Write-Output 'flake: settled'; exit 0",
        };
    }

    private int RunCount(string name) =>
        int.Parse(File.ReadAllText(Path.Combine(_dir, $"{name}-runs.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task RequiredGate_ThatFailsOnceThenPasses_IsRetriedAndTheBatteryIsGreen()
    {
        var results = await GateRunner.RunAllAsync(GatePlan(FlakyGate("flaky")));

        Assert.Equal(2, RunCount("flaky"));
        Assert.True(results[0].Passed);
        Assert.True(results[0].Retried);
        Assert.True(GateRunner.AllRequiredPassed(results));
        Assert.Contains("first attempt exited 1", results[0].Tail, StringComparison.Ordinal);
        Assert.Equal("flaky:OK-retry", GateRunner.Summary(results));
    }

    [Fact]
    public async Task RequiredGate_ThatFailsTwice_StaysRedAndTheFixPromptSaysItFailedTwice()
    {
        var plan = GatePlan(new GateConfig { Name = "broken", Command = "Write-Output 'real error'; exit 3", TimeoutMinutes = 1 });
        var results = await GateRunner.RunAllAsync(plan);

        Assert.False(results[0].Passed);
        Assert.True(results[0].Retried);
        Assert.Equal(3, results[0].ExitCode);
        Assert.False(GateRunner.AllRequiredPassed(results));
        Assert.Contains("failed twice", GateRunner.FailureDetails(results), StringComparison.Ordinal);
        Assert.Contains("real error", GateRunner.FailureDetails(results), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionalGate_IsNotRetried_BecauseItsFailureNeverBlocksAVerdict()
    {
        var results = await GateRunner.RunAllAsync(GatePlan(FlakyGate("advisory", optional: true)));

        Assert.Equal(1, RunCount("advisory"));
        Assert.False(results[0].Retried);
        Assert.True(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public async Task SkippedGate_IsNotRetried()
    {
        var plan = GatePlan(new GateConfig
        {
            Name = "later",
            Command = "exit 1",
            SkipIfMissing = $"absent-{Guid.NewGuid():N}.ps1",
            TimeoutMinutes = 1,
        });
        var results = await GateRunner.RunAllAsync(plan);

        Assert.True(results[0].Skipped);
        Assert.False(results[0].Retried);
    }

    [Fact]
    public void RetriedGate_IsChargedForBothAttempts()
    {
        var one = new GateResult("g", false, false, false, 1, TimeSpan.FromSeconds(10), "")
        {
            Retried = true,
            FirstAttemptDuration = TimeSpan.FromSeconds(4),
        };
        Assert.Equal(1.4m, one.EstimatedCostUsd(0.1m));
    }

    // ─────────────────────────────────── the failure line: duration vs last passing duration

    private (GateOrchestrator Gates, SqliteRunStore Store, RunState State) Battery(string name, PlanConfig plan)
    {
        var store = NewStore(name);
        var state = new RunState { RunId = $"run-{name}", CurrentStage = "S1" };
        store.InitializeRun(state.RunId, name, _dir, "main", "test");
        return (new GateOrchestrator(plan, state, new CollectingEventSink(), store), store, state);
    }

    private static async Task<List<string>> LinesFrom(GateOrchestrator gates)
    {
        var lines = new List<string>();
        await gates.RunBatteryAsync(_ => { }, (m, _) => lines.Add(m), _ => { }, CancellationToken.None, fastOnly: false);
        return lines;
    }

    [Fact]
    public async Task FailureLine_CarriesDurationAgainstTheLastPassingDurationOfTheSameGate()
    {
        var plan = GatePlan(new GateConfig { Name = "tests", Command = "Start-Sleep -Seconds 2; exit 1", TimeoutMinutes = 1 });
        var (gates, store, state) = Battery("vs-last-pass", plan);
        using var _ = store;
        // The same gate, at the same tier, passing in 6s earlier in this run.
        store.RecordGate(state.RunId, 1, "S1", "tests", "full", "session", "sha0", true, false, false, 0, 6000, "");

        var line = Assert.Single(await LinesFrom(gates), l => l.StartsWith("gate tests: FAIL", StringComparison.Ordinal));

        Assert.Contains("vs 6s when it last passed", line, StringComparison.Ordinal);
        Assert.Contains("%", line, StringComparison.Ordinal);
        Assert.Contains("after retry", line, StringComparison.Ordinal); // it failed twice, and the line says which
    }

    [Fact]
    public async Task FailureLine_SaysPlainlyWhenNoPassingRunIsOnRecord()
    {
        var plan = GatePlan(new GateConfig { Name = "tests", Command = "exit 1", TimeoutMinutes = 1 });
        var (gates, store, _) = Battery("no-record", plan);
        using var _s = store;

        var line = Assert.Single(await LinesFrom(gates), l => l.StartsWith("gate tests: FAIL", StringComparison.Ordinal));

        Assert.Contains("no passing run of this gate on record", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassOnRetry_IsVisibleOnTheGateLine_NotSilentlyReportedAsAPlainPass()
    {
        var (gates, store, _) = Battery("pass-on-retry", GatePlan(FlakyGate("flaky")));
        using var _s = store;

        var line = Assert.Single(await LinesFrom(gates), l => l.StartsWith("gate flaky:", StringComparison.Ordinal));

        Assert.Contains("PASS on retry", line, StringComparison.Ordinal);
        Assert.Contains("the first attempt failed after", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LastPassingDuration_IgnoresCachedSkippedAndFailedRows()
    {
        using var store = NewStore("last-pass-lookup");
        store.InitializeRun("r", "lookup", _dir, "main", "test");
        store.RecordGate("r", 1, "S1", "build", "fast", "session", "s1", true, false, false, 0, 5000, "");
        store.RecordGate("r", 2, "S1", "build", "fast", "session", "s2", false, false, false, 1, 9000, "");
        store.RecordGate("r", 3, "S1", "build", "fast", "session", "s3", true, false, false, 0, 0, "");     // cached
        store.RecordGate("r", 4, "S1", "build", "fast", "session", "s4", true, true, false, 0, 4000, "");   // skipped

        Assert.Equal(5000, store.GetLastPassingGateDurationMs("r", "build", "fast"));
        Assert.Null(store.GetLastPassingGateDurationMs("r", "build", "full"));   // a different tier is a different measurement
        Assert.Null(store.GetLastPassingGateDurationMs("r", "tests", "fast"));
    }

    // ───────────────────────────────────────────── end to end: a real run, a real live bg child

    /// <summary>
    /// The whole point, driven through the real orchestrator: a session leaves a background child
    /// running, and the gate battery does not start until that child has exited. Before SC4.1 the
    /// battery started the instant the agent process died.
    /// </summary>
    [Fact]
    public async Task LiveRun_TheGateBatteryDoesNotStartUntilTheSessionsBgChildHasExited()
    {
        var repo = Path.Combine(_dir, "live-repo");
        Directory.CreateDirectory(repo);
        Git("init -b main", repo);
        Git("config user.email sc41@test", repo);
        Git("config user.name sc41", repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# SC4.1 rig");
        Git("add README.md", repo);
        Git("commit -m initial --no-gpg-sign", repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
            "# SC4.1\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| S1.1 | rig checkpoint | TODO | | |\n");

        var started = Path.Combine(repo, "agent-started.txt");
        var agent = Path.Combine(repo, "fake-agent.cmd");
        await File.WriteAllTextAsync(agent, string.Join("\r\n",
            "@echo off",
            "echo started> agent-started.txt",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"working\"}}",
            "ping -n 5 127.0.0.1 > NUL",
            "echo agent ran> deliverable.txt",
            "git add deliverable.txt",
            "git commit -m \"feat: rig deliverable\" --no-gpg-sign",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "exit /b 0",
            ""));

        var plan = new PlanConfig
        {
            Name = "SC41SettlePlan",
            Repo = repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "S1", Title = "Rig", Sessions = 1 } },
            Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", agent, "{prompt}" }, Provider = "opencode" },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "exit 0", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        var store = host.Services.GetRequiredService<IRunStore>();

        var runTask = orchestrator.RunAsync(CancellationToken.None);

        // Register the child only once the agent is actually up: the run's own orphan reap runs
        // before the first session and would tree-kill a pid registered any earlier.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(started) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(File.Exists(started), "the fake agent never started");
        var child = Sleeper(9); // ~8s: outlives the agent, so the battery has to wait for it
        store.TrackPid(child.Id, state.RunId, "bg:rig-build", "S1", 1, DateTime.UtcNow);

        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromMinutes(3)));

        var log = await File.ReadAllTextAsync(Path.Combine(plan.StateDir, "conductor.log"));
        var exited = log.IndexOf("session #1 exited", StringComparison.Ordinal);
        var holding = log.IndexOf("holding gates up to", StringComparison.Ordinal);
        var settled = log.IndexOf("bg child(ren) exited after", StringComparison.Ordinal);
        var gate = log.IndexOf("gate smoke: PASS", StringComparison.Ordinal);

        Assert.True(exited >= 0 && holding > exited, $"no settle after the session exited:\n{log}");
        Assert.True(settled > holding, $"the settle never reported the children gone:\n{log}");
        Assert.True(gate > settled, $"the battery ran before the settle finished:\n{log}");
        Assert.True(child.HasExited);

        // and it really waited — not a zero-length formality
        var waited = double.Parse(
            log[(settled + "bg child(ren) exited after ".Length)..].Split('s')[0],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(waited >= 2.0, $"the battery only waited {waited}s for a child that had ~4s left to live");
    }

    private static void Git(string args, string cwd) =>
        ProcessRunner.Run("git", args.Split(' ', StringSplitOptions.RemoveEmptyEntries), cwd,
            TimeSpan.FromSeconds(30), CancellationToken.None);
}
