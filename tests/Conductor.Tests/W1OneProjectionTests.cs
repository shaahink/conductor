using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W1.4 truth gates (Category=Integration) — every view is the SAME graph projection. A verdict
/// flip is visible on the Kanban AND the /state sidebar before the next session starts (G11/G6);
/// an idle drag-to-Done over the wire records a real claim — graph, tracker view, and schedule
/// all follow, confirmation stays with the verdict engine.
/// </summary>
public sealed class W1OneProjectionTests
{
    private static string Tracker(params string[] rows) =>
        "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
        + string.Join("\n", rows) + "\n";

    private static async Task<string> ScaffoldRepoAsync(string repo, string trackerBody)
    {
        Directory.CreateDirectory(repo);
        ProcResult Git(string args) => ProcessRunner.Run("git",
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Git("init -b main");
        Git("config user.email w14@test");
        Git("config user.name W14");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
        Git("add README.md");
        Git("commit -m init --no-gpg-sign");
        await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"), trackerBody);
        var agentScript = Path.Combine(repo, "fake-agent.cmd");
        await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"working...\"}}",
            "ping -n 6 127.0.0.1 >nul",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "exit /b 0",
            ""));
        return agentScript;
    }

    private static async Task<PlanConfig> WritePlanAsync(string repo, string agentScript,
        int? maxSessionsCap, params StageConfig[] stages)
    {
        var planPath = Path.Combine(repo, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "w14-live",
            Repo = repo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [.. stages],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
            Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
        };
        seed.Limits.MaxSessions = maxSessionsCap;
        seed.Report.Commit = false;
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return PlanConfig.Load(planPath);
    }

    private static int ProbeFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VerdictFlip_IsOnTheBoardAndTheSidebar_BeforeTheNextSession()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w14a-{Guid.NewGuid():N}");
        using var http = new HttpClient();
        try
        {
            var agentScript = await ScaffoldRepoAsync(repo, Tracker("| H0.1 | the item | TODO | | |"));
            // limits.maxSessions=1: the run PARKS at the boundary after session 1 — the exact
            // "before the next session starts" moment, with the control plane still up.
            var plan = await WritePlanAsync(repo, agentScript, maxSessionsCap: 1,
                new StageConfig { Id = "H0", Title = "Delivered", Sessions = 2 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0,
                    ControlPlane: true, ControlPlanePort: ProbeFreePort()), consoleSink: false);
            var server = host.Services.GetRequiredService<Conductor.Core.Http.ControlPlaneServer>();
            Assert.True(server.Start(), "control plane failed to bind");
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

            using var cts = new CancellationTokenSource();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // Claim during session 1, exactly the task --done way (second store instance).
            var engineStore = host.Services.GetRequiredService<IRunStore>();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                   && !engineStore.ReadAllEvents(state.RunId).OfType<SessionStarted>().Any())
                await Task.Delay(50, CancellationToken.None);
            using (var cli = new SqliteRunStore(Path.Combine(repo, ".conductor", "run.db"),
                       NullLogger<SqliteRunStore>.Instance))
                cli.UpdateCheckpoint(state.RunId, "H0.1", "DONE", "fake1234", "claimed", source: "agent");

            deadline = DateTime.UtcNow.AddSeconds(90);
            while (!(state.Status == RunStatus.Paused && state.ParkedBySessionCap) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.ParkedBySessionCap, "run should park at the boundary after session 1");
            Assert.False(runTask.IsCompleted); // still up — this IS "before the next session"

            // The Kanban: the claimed card is done, checkpoint-kind, stage-tagged.
            var tasksJson = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/tasks", cts.Token);
            using (var tasks = JsonDocument.Parse(tasksJson))
            {
                var card = Assert.Single(tasks.RootElement.GetProperty("tasks").EnumerateArray(),
                    c => c.GetProperty("taskId").GetString() == "H0.1");
                Assert.Equal("done", card.GetProperty("status").GetString());
                Assert.Equal("checkpoint", card.GetProperty("kind").GetString());
                Assert.Equal("H0", card.GetProperty("stageId").GetString());
            }

            // The /state sidebar: SAME fold, same answer — G11's split is impossible.
            var stateJson = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/state", cts.Token);
            using (var snap = JsonDocument.Parse(stateJson))
            {
                Assert.Equal(1, snap.RootElement.GetProperty("doneCount").GetInt32());
                var stage = Assert.Single(snap.RootElement.GetProperty("stages").EnumerateArray());
                Assert.Equal(1, stage.GetProperty("done").GetInt32());
                var cp = Assert.Single(stage.GetProperty("checkpoints").EnumerateArray());
                Assert.Equal("H0.1", cp.GetProperty("id").GetString());
                Assert.StartsWith("DONE", cp.GetProperty("status").GetString(), StringComparison.Ordinal);
            }

            await cts.CancelAsync();
            await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IdleDragToDone_RecordsAClaim_TrackerAndScheduleFollow()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w14b-{Guid.NewGuid():N}");
        using var http = new HttpClient();
        try
        {
            var agentScript = await ScaffoldRepoAsync(repo, Tracker("| H0.1 | drag me | TODO | | |"));
            var plan = await WritePlanAsync(repo, agentScript, maxSessionsCap: null,
                new StageConfig { Id = "H0", Title = "Board Stage", Sessions = 1 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0,
                    ControlPlane: true, ControlPlanePort: ProbeFreePort(), StartPaused: true), consoleSink: false);
            var server = host.Services.GetRequiredService<Conductor.Core.Http.ControlPlaneServer>();
            Assert.True(server.Start(), "control plane failed to bind");
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

            using var cts = new CancellationTokenSource();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
                await Task.Delay(50, CancellationToken.None);
            Assert.Equal(RunStatus.Paused, state.Status);

            // Drag the checkpoint card to Done over the wire — while nothing is running.
            using var body = new StringContent("""{"taskId":"H0.1","status":"done"}""",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://127.0.0.1:{server.Port}/tasks/update", body, cts.Token);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // The claim landed in the graph — with human provenance, unconfirmed (the verdict
            // engine still owns confirmation) — and the tracker view followed at once, so the
            // engine's schedule sees DONE too. Never a silent no-op.
            var store = host.Services.GetRequiredService<IRunStore>();
            var row = Assert.Single(store.GetCheckpoints(state.RunId));
            Assert.Equal("DONE", row.Status);
            Assert.False(row.Confirmed);
            var claim = Assert.Single(store.ReadAllEvents(state.RunId).OfType<TaskStatusChanged>(),
                e => e.Status == "done");
            Assert.Equal("human", claim.Source);
            var tracker = await File.ReadAllTextAsync(Path.Combine(repo, "TRACKER.md"), cts.Token);
            Assert.Contains("DONE", tracker, StringComparison.Ordinal);

            await cts.CancelAsync();
            await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }
}
