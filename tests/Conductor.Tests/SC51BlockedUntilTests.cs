using Conductor.Http;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Hosting;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC5.1 — the rules of a declared wait. Every refusal here is a message the AGENT reads at the
/// moment it can still do something else, which is the whole reason the parse is shared: the CLI,
/// the MCP tool and the run loop apply the same judgement, so a wait cannot be accepted at one
/// ingress and silently dropped at another.
/// </summary>
public sealed class BlockedUntilRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AcceptsAFutureInstantWithAReason()
    {
        var (until, error) = BlockedUntilRequest.Parse("2026-07-31T15:12:00Z", "vercel window 100/100", Now);
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 15, 12, 0, TimeSpan.Zero), until);
    }

    /// <summary>An offset-bearing timestamp is normalised, not rejected: an agent reading a rate-limit
    /// header gets whatever the service prints, and the engine sleeps on instants, not on wall clocks.</summary>
    [Fact]
    public void NormalisesAnOffsetToUtc()
    {
        var (until, error) = BlockedUntilRequest.Parse("2026-07-31T17:12:00+02:00", "reset", Now);
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 15, 12, 0, TimeSpan.Zero), until);
    }

    [Fact]
    public void RefusesAPastInstant()
    {
        var (until, error) = BlockedUntilRequest.Parse("2026-07-31T11:59:00Z", "already open", Now);
        Assert.Null(until);
        Assert.Contains("not in the future", error, StringComparison.Ordinal);
    }

    /// <summary>The reason is not decoration. It is the thing the waking session reads INSTEAD of
    /// re-deriving the window — the exact $4.44 sk #1 paid twice.</summary>
    [Fact]
    public void RefusesAWaitWithNoReason()
    {
        var (until, error) = BlockedUntilRequest.Parse("2026-07-31T15:12:00Z", "   ", Now);
        Assert.Null(until);
        Assert.Contains("reason is required", error, StringComparison.Ordinal);
    }

    /// <summary>A run that can be put to sleep indefinitely by one session is a worse failure than the
    /// one this feature removes. Past the ceiling the honest answer is a human.</summary>
    [Fact]
    public void RefusesAWaitBeyondTheCeiling()
    {
        var beyond = Now.Add(BlockedUntilRequest.MaxWait).AddMinutes(1);
        var (until, error) = BlockedUntilRequest.Parse(beyond.ToString("O"), "quarterly release train", Now);
        Assert.Null(until);
        Assert.Contains("ceiling", error, StringComparison.Ordinal);
        Assert.Contains("HUMAN:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesSomethingThatIsNotATimestamp()
    {
        var (until, error) = BlockedUntilRequest.Parse("in about three hours", "reset", Now);
        Assert.Null(until);
        Assert.Contains("ISO 8601", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCarriesTheInstantTheCountdownAndTheReason()
    {
        var text = BlockedUntilRequest.Describe(Now.AddHours(3).AddMinutes(12), "vercel window 100/100", Now);
        Assert.Contains("2026-07-31 15:12:00Z", text, StringComparison.Ordinal);
        Assert.Contains("3h12m from now", text, StringComparison.Ordinal);
        Assert.Contains("vercel window 100/100", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// SC5.1 — the surfaces. A run asleep on a window must not read as a run that quietly stopped, and
/// once the window has opened it must stop claiming to be waiting: a stale park sentence is exactly
/// the lie SC2.2 took out of "what hurt".
/// </summary>
public sealed class SC51WaitingSurfacesTests : IDisposable
{
    private const string PlanName = "sc51-surfaces";
    private const string RunId = "run-sc51";
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly PlanConfig _plan;

    public SC51WaitingSurfacesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conductor-sc51s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        _dbPath = Path.Combine(_dir, ".conductor", "run.db");
        _plan = new PlanConfig
        {
            Name = PlanName,
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "D1", Title = "first", Sessions = 1 } },
            Agent = new AgentConfig { Command = "opencode", Args = { "run", "{prompt}" } },
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { }
    }

    private StatusReport BuildReport(params ConductorEvent[] events)
    {
        using (var store = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId(RunId);
            store.InitializeRun(RunId, PlanName, _dir, null, null);
            foreach (var e in events) store.Emit(e);
            store.FlushEvents();
        }
        using var read = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
        return StatusReportBuilder.Build(_plan, read, (_, _) => false);
    }

    [Fact]
    public void Status_SaysWaitingUntilAndWhy_WhileTheWindowIsShut()
    {
        var report = BuildReport(
            new RunStarted { Plan = PlanName, Repo = _dir },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "BlockedUntil" },
            new RunBlockedUntil
            {
                UntilUtc = DateTimeOffset.UtcNow.AddHours(2),
                Reason = "vercel deploy window 100/100, next slot 15:12",
                StageId = "D1",
                FromSession = 1,
            });

        Assert.Equal("waiting", report.Kind);
        Assert.Contains("waiting until", report.Verdict, StringComparison.Ordinal);
        Assert.Contains("vercel deploy window", report.Verdict, StringComparison.Ordinal);
        // A correctly-blocked session is not a wound: it must not turn up under "what hurt".
        Assert.Null(report.WhatHurt);
    }

    [Fact]
    public void Status_StopsSayingWaiting_OnceTheWindowHasOpened()
    {
        var report = BuildReport(
            new RunStarted { Plan = PlanName, Repo = _dir },
            new StageEntered { StageId = "D1" },
            new SessionStarted { Number = 1, StageId = "D1", Kind = "Deliver" },
            new SessionFinished { Number = 1, StageId = "D1", Outcome = "BlockedUntil" },
            new RunBlockedUntil
            {
                UntilUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                Reason = "window that has since opened",
                StageId = "D1",
                FromSession = 1,
            });

        Assert.Equal("idle", report.Kind);
        Assert.DoesNotContain("waiting until", report.Verdict, StringComparison.Ordinal);
        Assert.Contains("window opened", report.Verdict, StringComparison.Ordinal);
    }

    /// <summary>The snapshot half of the wire. NOTE this is deliberately NOT the whole /state test —
    /// see <see cref="SC51StateEndpointTests"/>: <c>GET /state</c> folds the event log and re-stamps
    /// the live control fields by hand, so a snapshot that carries the wait proves nothing about what
    /// the Face is actually served. A live rig served <c>status: Waiting</c> with an empty
    /// <c>blockedUntilUtc</c> while this assertion was green.</summary>
    [Fact]
    public void State_CarriesTheInstantAndTheReason()
    {
        var until = DateTime.UtcNow.AddHours(1);
        var state = new RunState
        {
            RunId = RunId,
            PlanName = PlanName,
            Status = RunStatus.Waiting,
            CurrentStage = "D1",
            BlockedUntilUtc = until,
            BlockedReason = "npm registry 429 until the top of the hour",
            BlockedSinceUtc = DateTime.UtcNow,
        };

        var snap = SnapshotBuilder.Build(_plan, state, new TrackerSnapshot());
        var dto = ControlPlaneDto.FromSnapshot(snap, RunId, _dir, _dir);

        Assert.Equal(until, dto.BlockedUntilUtc);
        Assert.Equal("npm registry 429 until the top of the hour", dto.BlockedReason);
        Assert.Equal("Waiting", dto.Status);

        // And it survives the wire, camelCased like the rest of the spine.
        var json = JsonSerializer.Serialize(dto, ControlPlaneJsonContext.Default.StateDto);
        Assert.Contains("\"blockedUntilUtc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"blockedReason\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void State_OmitsTheWaitWhenNothingIsWaiting()
    {
        var state = new RunState { RunId = RunId, PlanName = PlanName, CurrentStage = "D1" };
        var dto = ControlPlaneDto.FromSnapshot(
            SnapshotBuilder.Build(_plan, state, new TrackerSnapshot()), RunId, _dir, _dir);

        Assert.Null(dto.BlockedUntilUtc);
        Assert.Null(dto.BlockedReason);
        var json = JsonSerializer.Serialize(dto, ControlPlaneJsonContext.Default.StateDto);
        Assert.DoesNotContain("blockedUntilUtc", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_CarriesAWaitingLine()
    {
        var state = new RunState
        {
            RunId = RunId,
            PlanName = PlanName,
            Status = RunStatus.Waiting,
            CurrentStage = "D1",
            BlockedUntilUtc = DateTime.UtcNow.AddMinutes(90),
            BlockedReason = "vercel deploy window 100/100, next slot 15:12",
            BlockedSinceUtc = DateTime.UtcNow,
        };

        var md = Reporter.Build(_plan, state, new TrackerSnapshot(), lastGates: null);

        Assert.Contains("**Waiting:**", md, StringComparison.Ordinal);
        Assert.Contains("waiting until", md, StringComparison.Ordinal);
        Assert.Contains("vercel deploy window", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_SaysNothingAboutWaitingWhenTheRunIsNotWaiting()
    {
        var state = new RunState { RunId = RunId, PlanName = PlanName, CurrentStage = "D1" };
        var md = Reporter.Build(_plan, state, new TrackerSnapshot(), lastGates: null);
        Assert.DoesNotContain("**Waiting:**", md, StringComparison.Ordinal);
    }
}

/// <summary>
/// SC5.1 — the wait as <c>GET /state</c> actually serves it, over a real socket.
/// </summary>
/// <remarks>
/// This exists because a snapshot-level assertion was not enough and said so on a live rig. The
/// endpoint folds the event log — which by design carries only the event-sourced spine — and then
/// re-stamps the transient control fields (status, attention, …) from the live <c>RunState</c> by
/// hand. A field added to <c>RunState</c> and to <c>SnapshotBuilder</c> but not to that stamp list
/// reaches every other surface and arrives null here, which is how the rig ended up serving
/// <c>status: Waiting</c> next to an empty <c>blockedUntilUtc</c>.
/// </remarks>
public sealed class SC51StateEndpointTests : IDisposable
{
    private const string RunId = "run-sc51-state";
    private readonly string _dir;
    private readonly string _runDbPath;
    private readonly SqliteRunStore _store;
    private readonly PlanConfig _plan;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public SC51StateEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conductor-sc51w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        _runDbPath = Path.Combine(_dir, ".conductor", "run.db");
        _store = new SqliteRunStore(_runDbPath, NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _plan = new PlanConfig
        {
            Name = "sc51-state",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 } },
        };
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"),
            "# T\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| S1.1 | first | TODO | | |\n");
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int FreeLoopbackPort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    [Fact]
    public async Task GetState_ServesTheWaitFromTheLiveRunState_NotTheFold()
    {
        var until = DateTime.UtcNow.AddHours(2);
        var live = new RunState
        {
            RunId = RunId,
            Status = RunStatus.Waiting,
            CurrentStage = "S1",
            BlockedUntilUtc = until,
            BlockedReason = "upstream deploy window 100/100",
            BlockedSinceUtc = DateTime.UtcNow,
        };
        _store.Emit(new RunStarted { Plan = _plan.Name, Repo = _dir });
        _store.Emit(new StageEntered { StageId = "S1", Title = "Stage One" });
        _store.FlushEvents();

        var server = new ControlPlaneServer(_plan, live, _store, _inbox,
            new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start(), "control plane failed to bind");
        try
        {
            _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
            var body = await _http.GetStringAsync($"http://127.0.0.1:{server.Port}/state");
            using var doc = JsonDocument.Parse(body);

            Assert.Equal("Waiting", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("upstream deploy window 100/100",
                doc.RootElement.GetProperty("blockedReason").GetString());
            Assert.Equal(until.ToString("O")[..19],
                doc.RootElement.GetProperty("blockedUntilUtc").GetDateTime().ToString("O")[..19]);
        }
        finally { server.Dispose(); }
    }

    /// <summary>And it is absent, not zeroed, when the run is not waiting — "no wait" and "a wait at
    /// the epoch" must not render the same.</summary>
    [Fact]
    public async Task GetState_OmitsTheWait_WhenTheRunIsNotWaiting()
    {
        var live = new RunState { RunId = RunId, Status = RunStatus.Running, CurrentStage = "S1" };
        _store.Emit(new RunStarted { Plan = _plan.Name, Repo = _dir });
        _store.FlushEvents();

        var server = new ControlPlaneServer(_plan, live, _store, _inbox,
            new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start(), "control plane failed to bind");
        try
        {
            _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
            var body = await _http.GetStringAsync($"http://127.0.0.1:{server.Port}/state");
            Assert.DoesNotContain("blockedUntilUtc", body, StringComparison.Ordinal);
            Assert.DoesNotContain("blockedReason", body, StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }
}

/// <summary>
/// SC5.1 live — the promise itself, measured end to end. A real orchestrator drives a real git repo;
/// the stand-in agent calls the REAL freshly-built <c>conductor task --blocked-until</c> in its own
/// process, exactly as an agent does. What is asserted is what field notes 2026-07-29 (sk-platform
/// #1) paid $51.98 not to have: the engine sleeps on the window, wakes itself, spawns one more
/// session, and burns no attempt doing it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC51BlockedUntilLiveTests : IDisposable
{
    /// <summary>Long enough that the CLI still sees a future instant after host build and session
    /// spawn, short enough that the test is not a coffee break. A window that has already closed is
    /// loud rather than silent: the agent records the CLI's exit code and the assertions name it.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(18);

    private readonly string _repo;

    public SC51BlockedUntilLiveTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sc51-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "sc51@test");
        GitRun("config", "user.name", "SC51 Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# SC5.1 repo");
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# SC5.1 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| H0.1 | first checkpoint | TODO | | |\n" +
            "| H0.2 | second checkpoint | TODO | | |\n");
        GitRun("add", "-A");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception) { }
    }

    private ProcResult GitRun(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
        return r;
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset t) =>
        t.AddTicks(-(t.UtcTicks % TimeSpan.TicksPerSecond));

    private static string ConductorExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "conductor.exe");
        Assert.True(File.Exists(exe), $"the freshly-built CLI must sit beside the test assembly: {exe}");
        return exe;
    }

    private PlanConfig BuildPlan()
    {
        var plan = new PlanConfig
        {
            Name = "SC51Plan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "SC51", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", "placeholder", "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;
        return plan;
    }

    /// <summary>The plan the CLI resolves with <c>--plan</c>, serialised from the very object the
    /// engine runs — so the CLI cannot end up writing to a different run's database than the live
    /// engine is reading.</summary>
    private string WritePlanFile(PlanConfig plan)
    {
        var path = Path.Combine(_repo, "sc51.plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));
        return path;
    }

    /// <summary>One script, two behaviours, chosen by a marker file — the same agent an engine
    /// re-spawns. First run: declare the wait through the CLI. Second: do the work.</summary>
    private string WriteAgent(string planPath, DateTimeOffset until)
    {
        var path = Path.Combine(_repo, "sc51-agent.cmd");
        var call = "\"" + ConductorExe() + "\" task --plan \"" + planPath + "\" --blocked-until "
                   + until.ToString("yyyy-MM-ddTHH:mm:ssZ")
                   + " --reason \"deploy window 100/100, next slot at the timestamp\"";
        File.WriteAllText(path, string.Join("\r\n",
        [
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SC5.1 stand-in agent.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "if exist blocked.marker goto deliver",
            "echo blocked> blocked.marker",
            call,
            // Leading redirect on purpose. `echo exit=%ERRORLEVEL%> file` expands to `...0> file`, and
            // cmd reads the digit immediately before `>` as a stream number — the redirect swallows
            // the value and the file comes back empty however the text is prefixed.
            ">blocked-exit.txt echo exit=%ERRORLEVEL%",
            "exit /b 0",
            ":deliver",
            "echo deliverable> deliverable.md",
            "git add deliverable.md",
            "git commit -m \"feat: the work the window was blocking\" --no-gpg-sign -- deliverable.md",
            "exit /b 0",
            "",
        ]));
        return path;
    }

    [Fact]
    public async Task BlockedSession_SleepsUntilTheWindow_ThenRespawnsOnce_BurningNoAttempt()
    {
        // Whole seconds: the agent writes the instant in the ISO form a human types, which carries no
        // sub-second part. Comparing against an untruncated "now + window" would fail by the fraction
        // the format drops and say the engine did not wait when it waited exactly as asked.
        var until = TruncateToSecond(DateTimeOffset.UtcNow.Add(Window));
        var plan = BuildPlan();
        var planPath = WritePlanFile(plan);
        plan.Agent.Args[1] = WriteAgent(planPath, until);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 2), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);

        // The CLI accepted the wait in its OWN process — without that, nothing below means anything.
        var exitPath = Path.Combine(_repo, "blocked-exit.txt");
        Assert.True(File.Exists(exitPath), "the stand-in agent never reached the CLI call");
        Assert.Equal("exit=0", (await File.ReadAllTextAsync(exitPath, CancellationToken.None)).Trim());

        Assert.Equal(2, state.History.Count);
        var blocked = state.History[0];
        var woken = state.History[1];

        Assert.Equal(SessionOutcome.BlockedUntil, blocked.Outcome);

        // The promise: no attempt burned, no fix queued. A queued fix would have made session 2 a Fix
        // session rather than a retry of the work the window was blocking.
        Assert.Equal(0, state.AttemptsThisStage);
        Assert.Null(state.PendingFix);
        Assert.Equal(SessionKind.Deliver, woken.Kind);

        // The ENGINE did the waiting, not a human sitting and watching the clock (sk #1's session #18).
        Assert.True(woken.StartedUtc >= until.UtcDateTime,
            $"session 2 started at {woken.StartedUtc:O}, before the window opened at {until:O} — the engine did not wait");
        // …and it really slept, rather than the window happening to be over by the time it looked. A
        // regression that dropped the sleep would still satisfy the line above on a slow machine.
        Assert.NotNull(blocked.EndedUtc);
        Assert.True(woken.StartedUtc - blocked.EndedUtc!.Value > TimeSpan.FromSeconds(5),
            $"only {(woken.StartedUtc - blocked.EndedUtc.Value).TotalSeconds:0.#}s between the blocked session ending and the next one starting — that is not a wait");

        // And the wait clears once honoured, so the run is not asleep forever.
        Assert.Null(state.BlockedUntilUtc);
        Assert.NotEqual(RunStatus.Waiting, state.Status);

        // The park is on the event spine, after the blocking session's finish event — which is what
        // makes `conductor status` answer "waiting until T" instead of "idle".
        using var store = new SqliteRunStore(Path.Combine(plan.StateDir, "run.db"),
            NullLogger<SqliteRunStore>.Instance);
        var events = store.ReadAllEvents(state.RunId);
        var request = Assert.Single(events.OfType<BlockedUntilRequested>());
        Assert.Contains("deploy window 100/100", request.Reason, StringComparison.Ordinal);
        var park = Assert.Single(events.OfType<RunBlockedUntil>());
        var finish1 = events.OfType<SessionFinished>().First(e => e.Number == 1);
        Assert.True(park.Seq > finish1.Seq, "the park event must land after the session's finish event");
    }
}
