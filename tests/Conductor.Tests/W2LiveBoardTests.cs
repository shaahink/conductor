using Conductor.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Hosting;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W2.2 truth gates — the board is live DURING a session, and there is one task-id allocator.
/// The agent's MCP writes used to sit in <c>mcp-journal.jsonl</c> until the session ended, so
/// <c>GET /tasks</c> lagged a whole session behind the work; and because the journal-fed graph and the
/// control plane's graph were separate snapshots, both could mint the same id for different cards and
/// <see cref="TaskGraph.Fold"/> (first-write-wins) would silently drop one.
/// </summary>
public sealed class W2LiveBoardTests
{
    private static string Rpc(object payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Unwrap the MCP <c>tools/call</c> content envelope to the tool's own payload (W2.1).</summary>
    private static JsonElement Payload(JsonElement response) =>
        JsonSerializer.Deserialize<JsonElement>(
            response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);

    /// <summary>Drive the MCP server exactly as the spawned child does: line-delimited JSON-RPC
    /// over stdio, against a store opened on the SAME run.db the engine is using.</summary>
    private static async Task<List<JsonElement>> McpExchange(McpTaskServer server, params string[] requests)
    {
        using var stdin = new StringReader(string.Join(Environment.NewLine, requests));
        await using var stdout = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.RunAsync(stdin, stdout, cts.Token);
        return [.. stdout.ToString()
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonSerializer.Deserialize<JsonElement>(s))];
    }

    /// <summary>The agent side of the wire: a second store instance on the same run.db, exactly as the
    /// spawned <c>conductor mcp-serve</c> child opens it.</summary>
    private sealed record AgentSide(McpTaskServer Mcp, SqliteRunStore Store) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private static AgentSide OpenAgentSide(string repo, string runId)
    {
        var stateDir = Path.Combine(repo, ".conductor");
        SqliteRunStore? store = null;
        try
        {
            // K3.1: the scratch, the events file and the journal are still in the working tree; the
            // DATABASE is not. The agent side has to open the one the engine actually resolved,
            // which is exactly what `mcp-serve --run-db` now receives.
            store = new SqliteRunStore(TestState.RunDb(repo), NullLogger<SqliteRunStore>.Instance);
            store.SetRunId(runId); // McpServeCommand does this before handing the store over
            var server = new McpTaskServer(
                Path.Combine(stateDir, "events.jsonl"), Path.Combine(stateDir, "mcp-journal.jsonl"),
                runId, store, stateDir, repo);
            server.Init();
            var side = new AgentSide(server, store);
            store = null; // ownership passes to the returned AgentSide
            return side;
        }
        finally { store?.Dispose(); }
    }

    private static int ProbeFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task ScaffoldAsync(string repo, string trackerRow, string agentBody)
    {
        Directory.CreateDirectory(repo);
        ProcResult Git(string a) => ProcessRunner.Run("git",
            a.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Git("init -b main");
        Git("config user.email w22@test");
        Git("config user.name W22");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
        Git("add README.md");
        Git("commit -m init --no-gpg-sign");
        await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
            "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + trackerRow + "\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "fake-agent.cmd"), agentBody);
    }

    private static async Task<PlanConfig> WritePlanAsync(string repo, int? maxSessions, params StageConfig[] stages)
    {
        var planPath = Path.Combine(repo, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "w22-live",
            Repo = repo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [.. stages],
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = ["/c", Path.Combine(repo, "fake-agent.cmd"), "{prompt}"],
                Provider = "opencode",
            },
            Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
        };
        seed.Limits.MaxSessions = maxSessions;
        seed.Report.Commit = false;
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return PlanConfig.Load(planPath);
    }

    private static void Nuke(string repo)
    {
        try { TestTemp.DeleteTree(repo); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task McpWritesFromARunningSession_AreOnTheBoardWithinOnePoll()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w22a-{Guid.NewGuid():N}");
        using var http = new HttpClient();
        try
        {
            // The agent stays alive ~12s: long enough to make its MCP calls and read the board back
            // WHILE it is still running. That "while" is the whole point — session end must not be
            // what publishes the work.
            await ScaffoldAsync(repo, "| H0.1 | the item | TODO | | |", string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"working...\"}}",
                "ping -n 13 127.0.0.1 >nul",
                "exit /b 0",
                ""));
            var plan = await WritePlanAsync(repo, maxSessions: 1,
                new StageConfig { Id = "H0", Title = "Live", Sessions = 1 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0,
                    ControlPlane: true, ControlPlanePort: ProbeFreePort()), consoleSink: false);
            var server = host.Services.GetRequiredService<Conductor.Http.ControlPlaneServer>();
            Assert.True(server.Start(), "control plane failed to bind");
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

            using var cts = new CancellationTokenSource();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            var engineStore = host.Services.GetRequiredService<IRunStore>();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline
                   && !engineStore.ReadAllEvents(state.RunId).OfType<SessionStarted>().Any())
                await Task.Delay(50, CancellationToken.None);
            Assert.Contains(engineStore.ReadAllEvents(state.RunId), e => e is SessionStarted);

            // The agent, mid-session, adds a sub-task and claims its checkpoint over MCP.
            using (var agent = OpenAgentSide(repo, state.RunId))
            {
                var responses = await McpExchange(agent.Mcp,
                    Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "H0.1", title = "discovered mid-session", order = 0 } } }),
                    Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "task_update", arguments = new { taskId = "H0.1", status = "in_progress" } } }));
                Assert.True(Payload(responses[0]).GetProperty("ok").GetBoolean());
                Assert.True(Payload(responses[1]).GetProperty("ok").GetBoolean());
            }

            // The session is STILL OPEN — the close-time journal fold has not run. Without this the
            // test would pass on the old code too, by writing after the session had already ended.
            Assert.DoesNotContain(engineStore.ReadAllEvents(state.RunId), e => e is SessionFinished);
            Assert.False(runTask.IsCompleted);

            // One poll of the board shows both writes.
            var tasksJson = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/tasks", cts.Token);
            using (var tasks = JsonDocument.Parse(tasksJson))
            {
                var cards = tasks.RootElement.GetProperty("tasks").EnumerateArray().ToList();
                var added = Assert.Single(cards, c => c.GetProperty("title").GetString() == "discovered mid-session");
                Assert.Equal("H0.1", added.GetProperty("checkpointId").GetString());
                Assert.Equal("subtask", added.GetProperty("kind").GetString());
                var claimed = Assert.Single(cards, c => c.GetProperty("taskId").GetString() == "H0.1");
                Assert.Equal("in_progress", claimed.GetProperty("status").GetString());
            }

            await cts.CancelAsync();
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
        finally { Nuke(repo); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TwoWriters_OneAllocator_NeitherCardIsDropped()
    {
        // The G10 shape: the control plane (owner drags "add card") and the agent's MCP server both
        // add a card under the same checkpoint. With two independent allocators both minted
        // `H0.1-a1` and the fold's first-write-wins threw one away. One log, one allocator.
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w22b-{Guid.NewGuid():N}");
        using var http = new HttpClient();
        try
        {
            await ScaffoldAsync(repo, "| H0.1 | the item | TODO | | |", string.Join("\r\n",
                "@echo off", "exit /b 0", ""));
            var plan = await WritePlanAsync(repo, maxSessions: null,
                new StageConfig { Id = "H0", Title = "Alloc", Sessions = 1 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0,
                    ControlPlane: true, ControlPlanePort: ProbeFreePort(), StartPaused: true), consoleSink: false);
            var server = host.Services.GetRequiredService<Conductor.Http.ControlPlaneServer>();
            Assert.True(server.Start(), "control plane failed to bind");
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

            using var cts = new CancellationTokenSource();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
                await Task.Delay(50, CancellationToken.None);
            Assert.Equal(RunStatus.Paused, state.Status);

            // Writer 1 — the owner, over HTTP.
            using (var body = new StringContent("""{"checkpointId":"H0.1","title":"from the board"}""",
                       Encoding.UTF8, "application/json"))
            {
                var resp = await http.PostAsync($"http://127.0.0.1:{server.Port}/tasks/add", body, cts.Token);
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            }

            // Writer 2 — the agent, over MCP, in its own process-shaped store.
            string agentTaskId;
            using (var agent = OpenAgentSide(repo, state.RunId))
            {
                var responses = await McpExchange(agent.Mcp,
                    Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "H0.1", title = "from the agent", order = 0 } } }));
                var result = Payload(responses[0]);
                Assert.True(result.GetProperty("ok").GetBoolean());
                agentTaskId = result.GetProperty("taskId").GetString()!;
            }

            // Distinct ids, and BOTH cards survive the fold.
            var tasksJson = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/tasks", cts.Token);
            using (var tasks = JsonDocument.Parse(tasksJson))
            {
                var cards = tasks.RootElement.GetProperty("tasks").EnumerateArray().ToList();
                var fromBoard = Assert.Single(cards, c => c.GetProperty("title").GetString() == "from the board");
                var fromAgent = Assert.Single(cards, c => c.GetProperty("title").GetString() == "from the agent");
                Assert.NotEqual(fromBoard.GetProperty("taskId").GetString(), fromAgent.GetProperty("taskId").GetString());
                Assert.Equal(agentTaskId, fromAgent.GetProperty("taskId").GetString());
            }

            await cts.CancelAsync();
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
        finally { Nuke(repo); }
    }

    [Fact]
    public async Task WithoutAStore_TheJournalIsStillTheSink()
    {
        // Standalone `conductor mcp-serve` (no run.db next to the events file) must keep working the
        // old way, and SessionRunner's session-end fold still picks that journal up.
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-w22c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var eventsPath = Path.Combine(dir, "events.jsonl");
            var journalPath = Path.Combine(dir, "mcp-journal.jsonl");
            await File.WriteAllTextAsync(eventsPath, JsonSerializer.Serialize(
                (ConductorEvent)new TaskAdded { RunId = "r-nostore", TaskId = "H0.1", CheckpointId = "H0.1", Title = "cp", Order = 1, Source = "seed" },
                EventJsonContext.Default.ConductorEvent) + Environment.NewLine);

            var mcp = new McpTaskServer(eventsPath, journalPath, "r-nostore");
            mcp.Init();
            var responses = await McpExchange(mcp,
                Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "H0.1", title = "journalled", order = 0 } } }));
            Assert.True(Payload(responses[0]).GetProperty("ok").GetBoolean());

            Assert.True(File.Exists(journalPath), "no store means the journal is the only durable sink");
            var journalled = EventLog.ReadAll(journalPath);
            Assert.Contains(journalled.OfType<TaskAdded>(), e => e.Title == "journalled");
        }
        finally { Nuke(dir); }
    }
}
