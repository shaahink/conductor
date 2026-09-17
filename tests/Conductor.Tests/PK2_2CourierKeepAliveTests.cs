using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Courier;
using Conductor.Hosting;
using Conductor.Models;

using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>PK2.2 / D2(c,d,e) - the courier's last words, the keep-alive trigger, and a run that restarts
/// a dead courier at its session boundary. Scratch state homes and a recorded scheduler throughout: no
/// test here starts, ends or registers a real scheduled task.</summary>
public sealed class PK2_2CourierKeepAliveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "pk22-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public PK2_2CourierKeepAliveTests()
    {
        _home = Path.Combine(_tmp, "state-home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // ── D2(d) the keep-alive trigger ─────────────────────────────────────────────────────

    [Fact]
    public void TheTaskCarriesAFiveMinuteKeepAliveBesideTheLogonTrigger()
    {
        var xml = XDocument.Parse(new CourierTask("pk22-scratch").BuildXml(@"C:\c\conductor-courier.exe", "--task-name \"pk22-scratch\"", null));
        var ns = xml.Root!.Name.Namespace;
        var triggers = xml.Root.Element(ns + "Triggers")!;

        Assert.NotNull(triggers.Element(ns + "LogonTrigger"));
        var keepAlive = Assert.Single(triggers.Elements(ns + "TimeTrigger"));
        Assert.Equal("PT5M", keepAlive.Element(ns + "Repetition")!.Element(ns + "Interval")!.Value);
        Assert.Equal("false", keepAlive.Element(ns + "Repetition")!.Element(ns + "StopAtDurationEnd")!.Value);
        Assert.Null(keepAlive.Element(ns + "Repetition")!.Element(ns + "Duration"));   // indefinitely
        Assert.True(DateTime.Parse(keepAlive.Element(ns + "StartBoundary")!.Value, CultureInfo.InvariantCulture) < Now.UtcDateTime);

        // Every trigger enabled, and a tick that finds the courier running starts nothing.
        Assert.All(triggers.Elements(), t => Assert.Equal("true", t.Element(ns + "Enabled")!.Value));
        Assert.Equal("IgnoreNew", xml.Root.Element(ns + "Settings")!.Element(ns + "MultipleInstancesPolicy")!.Value);
    }

    // ── D2(c) last words ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryExitTheRuntimeReportsBecomesALine()
    {
        var log = CourierLog.At(_home);
        var exits = new CourierExitJournal(log);

        Assert.Contains("WITHOUT Main returning: exit code 0", exits.OnProcessExit(0), StringComparison.Ordinal);
        Assert.StartsWith("courier run DIED (unhandled, terminating): InvalidOperationException: boom",
            exits.OnUnhandled(new InvalidOperationException("boom"), terminating: true), StringComparison.Ordinal);
        Assert.Contains("SIGTERM", exits.OnSignal(PosixSignal.SIGTERM), StringComparison.Ordinal);
        exits.Returned(3);
        Assert.Equal("courier process exit: Main returned 3", exits.OnProcessExit(3));

        var written = File.ReadAllText(log.Path);
        Assert.Contains("WITHOUT Main returning", written, StringComparison.Ordinal);
        Assert.Contains("DIED (unhandled", written, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", written, StringComparison.Ordinal);
        Assert.Contains("Main returned 3", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFaultSeamUnderstandsTwoKindsAndNothingElse()
    {
        Assert.Equal(("exit0", 10), CourierProgram.ParseFault("exit0:10"));
        Assert.Equal(("throw", 0), CourierProgram.ParseFault(" throw:0 "));
        foreach (var bad in new[] { null, "", "exit0", "exit1:5", "throw:-1", "throw:x", "EXIT0:5" })
            Assert.Null(CourierProgram.ParseFault(bad));
    }

    // ── D2(e) the run as second supervisor ────────────────────────────────────────────────

    private sealed class Scheduler
    {
        public List<string> Calls { get; } = [];
        public int RunExit { get; set; }

        public CourierTask Task(string name) => new(name, (_, args) =>
        {
            Calls.Add(args);
            if (args.StartsWith("/Query", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(new ShellResult(0,
                    "\"HOST\",\"\\" + name + "\",\"N/A\",\"Ready\",\"Interactive only\",\"17/09/2026 11:40:00\",\"1\",\"conductor\"\r\n", ""));
            if (args.StartsWith("/Run", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(new ShellResult(RunExit, "", RunExit == 0 ? "" : "ERROR: task is disabled"));
            return System.Threading.Tasks.Task.FromResult(new ShellResult(0, "", ""));
        });
    }

    private void Configure()
    {
        new CourierSettings
        {
            Chats = [new CourierChat("770000001", "admin")],
            Projects = [new CourierProject("pk22", _tmp)],
        }.Save(_home);
        Assert.True(CourierPrecedence.Configured(_home));
    }

    private void Presence(DateTimeOffset? lastPoll, string? task = "pk22-scratch") =>
        new CourierPresence(CourierProtocol.Version, 4242, "0.5.1", @"C:\c\conductor-courier.exe", task,
            Now.AddHours(-3), 47002, lastPoll).Write(_home);

    private CourierKeepAlive KeepAlive(Scheduler scheduler, bool pidAlive) =>
        new(_home, probe: _ => pidAlive ? Now.AddHours(-3) : null, tasks: scheduler.Task, now: () => Now);

    [Fact]
    public async Task AMachineWithNoCourierIsUntouched()
    {
        var scheduler = new Scheduler();
        Presence(Now.AddHours(-2));                      // a record, but no courier.json
        Assert.Null(await KeepAlive(scheduler, pidAlive: false).CheckAsync(0));
        Assert.Empty(scheduler.Calls);
    }

    [Theory]
    [InlineData(true, 30, "alive")]
    [InlineData(true, null, "unmetered")]
    public async Task ALiveOrUnmeteredCourierIsLeftAlone(bool pidAlive, int? pollSecondsAgo, string _)
    {
        Configure();
        Presence(pollSecondsAgo is { } s ? Now.AddSeconds(-s) : null);
        var scheduler = new Scheduler();
        Assert.Null(await KeepAlive(scheduler, pidAlive).CheckAsync(0));
        Assert.Empty(scheduler.Calls);
    }

    [Fact]
    public async Task ACleanlyStoppedCourierIsNotADeath()
    {
        Configure();
        var scheduler = new Scheduler();
        Assert.Null(await KeepAlive(scheduler, pidAlive: false).CheckAsync(0));
        Assert.Empty(scheduler.Calls);
    }

    [Fact]
    public async Task ADeadCourierIsStarted_AndTheLastRunIsReadBeforeTheStartOverwritesIt()
    {
        Configure();
        Presence(Now.AddMinutes(-40));
        var scheduler = new Scheduler();

        var attempt = await KeepAlive(scheduler, pidAlive: false).CheckAsync(restartsSoFar: 2);

        Assert.NotNull(attempt);
        Assert.True(attempt!.Started);
        Assert.Equal(CourierLife.Dead, attempt.Found);
        Assert.Equal(["/Query /TN \"pk22-scratch\" /V /FO CSV /NH", "/Run /TN \"pk22-scratch\""], scheduler.Calls);
        Assert.StartsWith("courier restarted by this run, 3rd time: found dead (last seen 2026-09-17 11:20:00Z, 40 min ago",
            attempt.Line, StringComparison.Ordinal);
        Assert.Contains("task \"pk22-scratch\" last run 1 (0x00000001) at 17/09/2026 11:40:00", attempt.Line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleCouriersHungInstanceIsEndedFirst_BecauseIgnoreNewWouldIgnoreTheStart()
    {
        Configure();
        Presence(Now.AddMinutes(-30));
        var scheduler = new Scheduler();

        var attempt = await KeepAlive(scheduler, pidAlive: true).CheckAsync(0);

        Assert.Equal(CourierLife.Stale, attempt!.Found);
        Assert.Equal(["/Query", "/End", "/Run"], scheduler.Calls.Select(c => c.Split(' ')[0]));
        Assert.Contains("1st time: found stale (last poll 30 min ago", attempt.Line, StringComparison.Ordinal);
        Assert.EndsWith("; ended the hung instance", attempt.Line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartTheSchedulerRefusesIsNotCountedAsARestart()
    {
        Configure();
        Presence(Now.AddMinutes(-40));
        var scheduler = new Scheduler { RunExit = 1 };
        var state = new RunState();
        var said = new List<string>();

        var changed = await KeepAlive(scheduler, pidAlive: false).AtBoundaryAsync(state, said.Add);

        Assert.False(changed);
        Assert.Null(state.CourierRestarts);
        Assert.StartsWith("courier not restarted: found dead", Assert.Single(said), StringComparison.Ordinal);
        Assert.EndsWith("this run could not start the task: ERROR: task is disabled", said[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachBoundaryRestartIsCountedOnTheRun()
    {
        Configure();
        Presence(Now.AddMinutes(-40));
        var state = new RunState();
        var said = new List<string>();
        var keepAlive = KeepAlive(new Scheduler(), pidAlive: false);

        Assert.True(await keepAlive.AtBoundaryAsync(state, said.Add));
        Assert.True(await keepAlive.AtBoundaryAsync(state, said.Add));

        Assert.Equal(2, state.CourierRestarts!.Count);
        Assert.StartsWith("courier restarted by this run, 2nd time", state.CourierRestarts.Last, StringComparison.Ordinal);
        Assert.Equal(Now.UtcDateTime, state.CourierRestarts.LastUtc);
        Assert.Equal(2, said.Count);
    }

    [Fact]
    public void OrdinalsAreEnglish_AtEveryNumber()
    {
        for (var n = 1; n <= 250; n++)
        {
            var expected = (n % 100) is >= 11 and <= 13 ? "th"
                : (n % 10) == 1 ? "st" : (n % 10) == 2 ? "nd" : (n % 10) == 3 ? "rd" : "th";
            Assert.Equal(n.ToString(CultureInfo.InvariantCulture) + expected, CourierKeepAlive.Ordinal(n));
        }
    }

    [Fact]
    public void ARunStateWithNoRestartWritesTheStateItAlwaysWrote()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new RunState(), PlanConfig.JsonOpts);
        Assert.DoesNotContain("courierRestarts", json, StringComparison.OrdinalIgnoreCase);

        var restarted = new RunState { CourierRestarts = new CourierRestarts(1, Now.UtcDateTime, "courier restarted by this run, 1st time") };
        var back = System.Text.Json.JsonSerializer.Deserialize<RunState>(
            System.Text.Json.JsonSerializer.Serialize(restarted, PlanConfig.JsonOpts), PlanConfig.JsonOpts)!;
        Assert.Equal(restarted.CourierRestarts, back.CourierRestarts);
    }

    // ── a real run, through its session boundary ──────────────────────────────────────────

    [Fact]
    public async Task ARunRestartsADeadCourierAtItsBoundary_AndTheOwnerQueueCarriesIt()
    {
        var plan = RigPlan();
        Configure();
        Presence(Now.AddMinutes(-40));
        var scheduler = new Scheduler();
        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        orchestrator.UseCourierKeepAlive(KeepAlive(scheduler, pidAlive: false));

        Assert.Equal(0, await orchestrator.RunAsync(CancellationToken.None));

        Assert.Single(state.History);
        Assert.Equal(1, state.CourierRestarts?.Count);
        Assert.Contains("/Run /TN \"pk22-scratch\"", scheduler.Calls);

        var log = await File.ReadAllTextAsync(Path.Combine(plan.StateDir, "conductor.log"));
        Assert.Contains("courier restarted by this run, 1st time: found dead", log, StringComparison.Ordinal);

        var queue = await File.ReadAllTextAsync(OwnerQueue.QueuePath(plan));
        Assert.Contains("courier restarted by this run, 1st time", queue, StringComparison.Ordinal);
        Assert.Contains("conductor courier status", queue, StringComparison.Ordinal);
    }

    /// <summary>A one-stage plan over a scratch git repo with a fake agent that says one line and exits.</summary>
    private PlanConfig RigPlan()
    {
        var repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(repo);
        void Git(params string[] args)
        {
            var r = ProcessRunner.Run("git", args, repo, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)}: {r.Output} {r.StdErr}");
        }
        Git("init", "-b", "main");
        Git("config", "user.email", "pk22@test");
        Git("config", "user.name", "PK2.2 rig");
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# PK2.2 rig\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n"
          + "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | rig | TODO | | |\n");
        var agent = Path.Combine(repo, "fake-agent.cmd");
        File.WriteAllText(agent, string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"step_start\"}",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"PK2.2 rig session.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":10,\"reasoning\":0,\"cache\":{\"read\":0}}}}",
            "exit /b 0", ""));
        Git("add", "-A");
        Git("commit", "-m", "chore: rig", "--no-gpg-sign");

        var plan = new PlanConfig
        {
            Name = "PK22Rig",
            Repo = repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Rig", Sessions = 1 } },
            Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", agent, "{prompt}" }, Provider = "opencode" },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;
        return plan;
    }

    [Fact]
    public async Task ADryRunStartsNoTask()
    {
        var plan = RigPlan();
        Configure();
        Presence(Now.AddMinutes(-40));
        var scheduler = new Scheduler();
        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: true, Once: true, MaxSessions: 0), consoleSink: false);
        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        orchestrator.UseCourierKeepAlive(KeepAlive(scheduler, pidAlive: false));
        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Empty(scheduler.Calls);
        Assert.Null(state.CourierRestarts);
    }
}
