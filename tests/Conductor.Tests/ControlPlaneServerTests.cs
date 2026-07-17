using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F5 curl-level contract tests (design doc's own stated gate for the control plane): a real
/// HttpListener bound to an ephemeral loopback port, exercised with real HTTP requests — no mocking
/// of the transport. Covers the read side (state/tasks built from run.db events, matching
/// what RunStateProjection/TaskGraph/SnapshotBuilder already produce elsewhere) and the write side
/// (POST /control enqueues onto the same inbox Orchestrator.PollInbox drains).
/// </summary>
public sealed class ControlPlaneServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-cps-{Guid.NewGuid():N}");
    private readonly string _transcriptPath;
    private readonly string _runDbPath;
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    private const string RunId = "run-cps";

    public ControlPlaneServerTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        _transcriptPath = Path.Combine(stateDir, "transcript.jsonl");
        _runDbPath = Path.Combine(stateDir, "run.db");
        _store = new SqliteRunStore(_runDbPath, NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _plan = new PlanConfig
        {
            Name = "cps-test",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 } },
        };
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"),
            "# T\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| S1.1 | first | DONE | abc123 | ev |\n");
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static int FreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private void WriteEvents(params ConductorEvent[] events)
    {
        var before = _store.ReadAllEvents(RunId).Count;
        foreach (var e in events)
            _store.Emit(e);
        // Emit persists via an async drain; the server reads the events table synchronously. Wait until
        // every event has landed so these wire tests are deterministic under parallel load rather than
        // racing the drain (which showed up as partially-folded state on a saturated suite run).
        var target = before + events.Length;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_store.ReadAllEvents(RunId).Count < target && DateTime.UtcNow < deadline)
            System.Threading.Thread.Sleep(10);
    }

    private (ControlPlaneServer server, int port) StartServer(RunState? state = null)
    {
        var port = FreeLoopbackPort();
        state ??= new RunState { RunId = RunId };
        var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind — cannot run contract tests");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        // server.Port, not the probe port: Start() scans forward when a parallel fixture grabbed
        // the probed port first, and requests must follow the server (same fix as P5RolloverTests).
        return (server, server.Port);
    }

    [Fact]
    public async Task GetState_ReturnsSnapshotBuiltFromEventLog()
    {
        WriteEvents(
            new RunStarted { Plan = "cps-test", Repo = _dir },
            new StageEntered { StageId = "S1", Title = "Stage One" });
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/state");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("cps-test", doc.RootElement.GetProperty("planName").GetString());
            Assert.Equal("S1", doc.RootElement.GetProperty("stageId").GetString());
            var stages = doc.RootElement.GetProperty("stages");
            Assert.Equal(1, stages.GetArrayLength());
            Assert.Equal("S1", stages[0].GetProperty("id").GetString());
            // The checkpoint row from TRACKER.md flows through SnapshotBuilder into the DTO.
            var checkpoints = stages[0].GetProperty("checkpoints");
            Assert.Equal(1, checkpoints.GetArrayLength());
            Assert.Equal("S1.1", checkpoints[0].GetProperty("id").GetString());
        }
        finally { server.Dispose(); }
    }

    // U1.1: the Face's Home panel names the whole workspace, so /state has to carry the whole
    // workspace. Golden frames can't catch a wire mismatch — only a real round-trip can.
    // The load-bearing assertion is stateDir: PlanConfig.StateDir is rooted at Repo, NOT PlanDir, so
    // a plan file living outside the repo (as it does here, and in the conductor repo itself) must
    // NOT move the state dir. The spec text says "<planDir>/.conductor"; the engine disagrees, and
    // the engine is what actually writes the files.
    [Fact]
    public async Task GetState_CarriesTheWorkspaceIdentity_WithStateDirRootedAtRepoNotPlanDir()
    {
        WriteEvents(new RunStarted { Plan = "cps-test", Repo = _dir });

        var planDir = Path.Combine(_dir, "plans");
        Directory.CreateDirectory(planDir);
        _plan.PlanFilePath = Path.Combine(planDir, "cps-test.plan.json");

        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/state");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            Assert.Equal(_dir, doc.RootElement.GetProperty("repo").GetString());
            Assert.Equal("TRACKER.md", doc.RootElement.GetProperty("tracker").GetString());
            Assert.Equal(planDir, doc.RootElement.GetProperty("planDir").GetString());

            var stateDir = doc.RootElement.GetProperty("stateDir").GetString();
            Assert.Equal(Path.Combine(_dir, ".conductor"), stateDir);
            Assert.Equal(_plan.StateDir, stateDir);
            Assert.DoesNotContain("plans", stateDir!.Replace(_dir, "", StringComparison.Ordinal),
                StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    // P5 follow-up: /state surfaces the set-rollover this-run override straight off the live
    // RunState — absent when there is no override (honest OFF-by-default), 0 when forced off,
    // the cap when set. The server holds the same instance the dispatcher mutates, so flipping
    // the override between GETs must be visible without a restart.
    [Fact]
    public async Task GetState_SurfacesTheSetRolloverOverride_AndOmitsItWhenClear()
    {
        WriteEvents(new RunStarted { Plan = "cps-test", Repo = _dir });
        var state = new RunState { RunId = RunId };
        var (server, port) = StartServer(state);
        try
        {
            async Task<JsonDocument> GetStateAsync()
            {
                var resp = await _http.GetAsync($"http://127.0.0.1:{port}/state");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            }

            using (var doc = await GetStateAsync())
                Assert.False(doc.RootElement.TryGetProperty("maxSessionTokensThisRun", out _),
                    "no override queued — the field must be absent, not null/0");

            state.MaxSessionTokensThisRun = 180000;
            using (var doc = await GetStateAsync())
                Assert.Equal(180000, doc.RootElement.GetProperty("maxSessionTokensThisRun").GetInt64());

            state.MaxSessionTokensThisRun = 0; // set-rollover off: forced OFF is data, not absence
            using (var doc = await GetStateAsync())
                Assert.Equal(0, doc.RootElement.GetProperty("maxSessionTokensThisRun").GetInt64());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetState_NoEventsYet_ReturnsDefaultSnapshotNot500()
    {
        // No WriteEvents call — events.jsonl doesn't exist. Must be "no progress yet", not a 500.
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/state");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetTasks_ReturnsTaskGraphFoldedFromEvents()
    {
        WriteEvents(
            new TaskAdded { TaskId = "t1", CheckpointId = "S1.1", Title = "Do the thing", Source = "agent", Order = 1 },
            new TaskStatusChanged { TaskId = "t1", Status = "in_progress" });
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/tasks");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var tasks = doc.RootElement.GetProperty("tasks");
            Assert.Equal(1, tasks.GetArrayLength());
            Assert.Equal("t1", tasks[0].GetProperty("taskId").GetString());
            Assert.Equal("in_progress", tasks[0].GetProperty("status").GetString());
        }
        finally { server.Dispose(); }
    }

    private async Task<HttpResponseMessage> PostJson(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }

    [Fact]
    public async Task PostNote_WritesLedgerRowTheBatteriesConsume()
    {
        _store.InitializeRun(RunId, "cps-test", _dir, null, null); // create the run row (ledger FKs to it)
        var (server, port) = StartServer();
        try
        {
            var resp = await PostJson(port, "/note", """{"content":"warm the cache","kind":"trap","stageId":"S1"}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var rows = _store.QueryLedger(RunId);
            Assert.Contains(rows, r => r.Content == "warm the cache" && r.Kind == "trap" && r.StageId == "S1");
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostNote_EmptyContent_Returns400NotAWrite()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostJson(port, "/note", """{"content":"   "}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Empty(_store.QueryLedger(RunId));
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostBug_ThenResolve_MovesItOutOfOpen()
    {
        _store.InitializeRun(RunId, "cps-test", _dir, null, null); // create the run row (bugs FKs to it)
        var (server, port) = StartServer();
        try
        {
            var create = await PostJson(port, "/bug", """{"title":"double-count cost","severity":"high","stageId":"S1"}""");
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            using var cdoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var id = cdoc.RootElement.GetProperty("id").GetInt64();
            Assert.True(id > 0);
            Assert.Contains(_store.QueryBugs(RunId, "open"), b => b.Id == id && b.Severity == "high");

            var resolve = await PostJson(port, "/bug/resolve", "{\"id\":" + id + "}");
            Assert.Equal(HttpStatusCode.Accepted, resolve.StatusCode);
            Assert.DoesNotContain(_store.QueryBugs(RunId, "open"), b => b.Id == id);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostBugResolve_UnknownId_Returns400()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostJson(port, "/bug/resolve", "{\"id\":99999}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetTimeline_FoldsSessionGateAndAttentionEvents()
    {
        // M5.1: /timeline folds the event spine into a visual timeline. This is the first test that
        // exercises the endpoint over the wire — it was shipped without one. The Go Face consumes this
        // exact JSON shape (conductor-face-go/internal/api TimelineEntryDto).
        WriteEvents(
            new RunStarted { Plan = "cps-test", Repo = _dir },
            new StageEntered { StageId = "S1", Title = "Stage One" },
            new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" },
            new GateFinished { Name = "build", Passed = true, DurationMs = 1200, Scope = "S1" },
            new SessionFinished { Number = 1, StageId = "S1", Outcome = "Advanced", CostUsd = 0.33m },
            new AttentionRequested { Reason = "needs a human" });
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/timeline");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var entries = doc.RootElement.GetProperty("entries");
            Assert.True(entries.GetArrayLength() >= 5, "expected session/gate/stage/attention entries");

            var kinds = entries.EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
            Assert.Contains("session", kinds);
            Assert.Contains("gate", kinds);
            Assert.Contains("stage", kinds);
            Assert.Contains("attention", kinds);

            // The finished session carries its cost onto the wire (camelCase field the Go DTO reads).
            var finished = entries.EnumerateArray().First(e =>
                e.GetProperty("kind").GetString() == "session" &&
                e.GetProperty("description").GetString()!.Contains("finished", StringComparison.Ordinal));
            Assert.Equal(0.33m, finished.GetProperty("costUsd").GetDecimal());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetLedger_ReturnsRecentEntries()
    {
        // M7.1: /ledger serves the knowledge ledger to the Face (Go DTO LedgerEntryDto).
        _store.InitializeRun(RunId, "cps-test", _dir, null, "test"); // FK parent for ledger rows
        _store.WriteLedger(RunId, 1, "S1", "finding", "the retry prompt must carry verifier findings");
        _store.WriteLedger(RunId, 2, "S1", "hand-edit", "engine bookkeeping — must NOT be surfaced");
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/ledger");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var entries = doc.RootElement.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength()); // hand-edit filtered out
            Assert.Equal("finding", entries[0].GetProperty("kind").GetString());
            Assert.Contains("verifier findings", entries[0].GetProperty("content").GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetBugs_ReturnsOpenBugsByDefault()
    {
        // M7.2: /bugs serves tracked bugs to the Face (Go DTO BugDto).
        _store.InitializeRun(RunId, "cps-test", _dir, null, "test"); // FK parent for bug rows
        var openId = _store.WriteBug(RunId, "stall breaker fires during long gate", "seen on the test gate", "high", "S1", 1);
        var closedId = _store.WriteBug(RunId, "already fixed", null, "low", "S1", 1);
        _store.UpdateBugStatus(RunId, closedId, "fixed", 2);
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/bugs");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var bugs = doc.RootElement.GetProperty("bugs");
            Assert.Equal(1, bugs.GetArrayLength()); // only the open one
            Assert.Equal(openId, bugs[0].GetProperty("id").GetInt64());
            Assert.Equal("high", bugs[0].GetProperty("severity").GetString());
            Assert.Equal("open", bugs[0].GetProperty("status").GetString());

            // ?status=all includes the closed one
            var allResp = await _http.GetAsync($"http://127.0.0.1:{port}/bugs?status=all");
            using var allDoc = JsonDocument.Parse(await allResp.Content.ReadAsStringAsync());
            Assert.Equal(2, allDoc.RootElement.GetProperty("bugs").GetArrayLength());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetPromptPreview_ReturnsCompiledPromptForStage()
    {
        // M5.5: /prompt/preview compiles the exact prompt that would be sent. Untested until now.
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/prompt/preview?stage=S1&kind=Deliver");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("prompt").GetString()),
                "compiled prompt must not be empty");
            Assert.Equal("Deliver", doc.RootElement.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("model").GetString()));
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetPromptPreview_UnknownStage_Returns404()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/prompt/preview?stage=NOPE&kind=Deliver");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetState_FoldsLiveTokenDeltaIntoSessionTicker()
    {
        // M5.4: cost/tokens must accrue DURING a session. Session #1 is started but not finished, so its
        // TokenDelta events are live spend the ticker should reflect — not zero-until-SessionFinished.
        WriteEvents(
            new RunStarted { Plan = "cps-test", Repo = _dir },
            new StageEntered { StageId = "S1" },
            new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" },
            new TokenDelta { SessionId = "1", Input = 1000, Output = 500, Reasoning = 200, CostUsd = 0.12m },
            new TokenDelta { SessionId = "1", Input = 500, Output = 250, CostUsd = 0.06m });
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/state");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.True(root.GetProperty("agentActive").GetBoolean(), "an unfinished session must read as active");
            Assert.Equal(0.18, root.GetProperty("sessionCostUsd").GetDouble(), 3);
            Assert.Equal(1500, root.GetProperty("sessionTokensInput").GetInt64());
            Assert.Equal(750, root.GetProperty("sessionTokensOutput").GetInt64());
            // The run total includes the in-flight session's live spend (nothing is in History yet).
            Assert.True(root.GetProperty("totalCostUsd").GetDouble() >= 0.18, "total must include live session spend");
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetState_SessionFinished_DoesNotDoubleCountLiveDeltas()
    {
        // Once the session finishes, its cost lives in History; the live fold must not be added on top.
        WriteEvents(
            new RunStarted { Plan = "cps-test", Repo = _dir },
            new StageEntered { StageId = "S1" },
            new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" },
            new TokenDelta { SessionId = "1", Input = 1000, Output = 500, CostUsd = 0.20m },
            new SessionFinished { Number = 1, StageId = "S1", Outcome = "Advanced", CostUsd = 0.20m });
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/state");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.False(root.GetProperty("agentActive").GetBoolean(), "a finished session must not read as active");
            Assert.Equal(0.20, root.GetProperty("totalCostUsd").GetDouble(), 3); // History cost only, not 0.40
        }
        finally { server.Dispose(); }
    }

    [Fact]
    [Trait("Category", "Integration")] // waits on the SSE poll cycle
    public async Task GetConsoleCurrent_StreamsRawSessionLogAsSse()
    {
        // M5.3: /console/current tails the current session's RAW agent stdout log.
        var logsDir = Path.Combine(_plan.StateDir, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "session-001.jsonl"),
            "{\"type\":\"assistant\",\"text\":\"raw agent line alpha\"}\n");
        var (server, port) = StartServer();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/console/current",
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            string? line;
            var saw = false;
            while (!saw && (line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("raw agent line alpha", StringComparison.Ordinal))
                    saw = true;
            }
            Assert.True(saw, "expected the raw console line as an SSE frame within the timeout");
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostControl_ValidCommand_EnqueuesAndReturns202()
    {
        var (server, port) = StartServer();
        try
        {
            using var content = new StringContent("""{"command":"pause"}""", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{port}/control", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            Assert.True(_inbox.TryDequeue(out var cmd));
            Assert.Equal(ControlAction.PauseAfterSession, cmd.Action);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostControl_WithStageIdAndForce_PreservesFullPayload()
    {
        // The whole point of widening PollControl (F5 prep): goto/rollback payload must survive
        // the HTTP ingress exactly like it does from control.json.
        var (server, port) = StartServer();
        try
        {
            using var content = new StringContent("""{"command":"goto","stageId":"S2"}""", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{port}/control", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            Assert.True(_inbox.TryDequeue(out var cmd));
            Assert.Equal(ControlAction.Goto, cmd.Action);
            Assert.Equal("S2", cmd.StageId);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostControl_UnrecognisedCommand_Returns400AndDoesNotEnqueue()
    {
        var (server, port) = StartServer();
        try
        {
            using var content = new StringContent("""{"command":"not-a-real-verb"}""", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{port}/control", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Empty(_inbox);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/nope");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    [Trait("Category", "Integration")] // waits on the SSE poll cycle (1s), not just a request/response
    public async Task GetEvents_StreamsExistingAndNewEventsAsSse()
    {
        WriteEvents(new RunStarted { Plan = "cps-test", Repo = _dir });
        var (server, port) = StartServer();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/events",
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            string? line;
            var sawRunStarted = false;
            while (!sawRunStarted && (line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("runStarted", StringComparison.Ordinal))
                    sawRunStarted = true;
            }
            Assert.True(sawRunStarted, "expected a 'runStarted' SSE frame within the timeout");
        }
        finally { server.Dispose(); }
    }

    [Fact]
    /// <summary>A taken port is the normal case when a second plan is running in another terminal, so the
    /// server scans forward to the next free one instead of giving up. The run that got there first keeps
    /// its port; the newcomer takes another and publishes it — which is why clients read the port from
    /// control-plane.json rather than assuming 4317.</summary>
    public void Start_PortAlreadyBound_ScansForwardToAFreePort()
    {
        var port = FreeLoopbackPort();
        var blocker = new HttpListener();
        blocker.Prefixes.Add($"http://127.0.0.1:{port}/");
        blocker.Start();
        try
        {
            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, port);
            var started = server.Start();

            Assert.True(started);                 // a busy port must not cost us the control plane
            Assert.NotEqual(port, server.Port);   // ...but it must not steal the other run's port either
            Assert.InRange(server.Port, port + 1, port + 19);

            server.Dispose();
        }
        finally { blocker.Stop(); blocker.Close(); }
    }

    [Fact]
    /// <summary>The bound port is published so a Face (or a second terminal) can attach without being told
    /// a number, and is removed on shutdown so nobody is ever pointed at a dead port.</summary>
    public void Start_PublishesDiscoveryFile_AndRemovesItOnDispose()
    {
        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start());

        var discovery = ControlPlaneServer.DiscoveryPath(_plan.StateDir);
        Assert.True(File.Exists(discovery));

        var info = JsonSerializer.Deserialize(File.ReadAllText(discovery), ControlPlaneJsonContext.Default.ControlPlaneInfo);
        Assert.NotNull(info);
        Assert.Equal(server.Port, info!.Port);
        Assert.Equal($"http://127.0.0.1:{server.Port}", info.BaseUrl);
        Assert.Equal(server.Token, info.Token); // clients read the write token from here

        server.Dispose();
        Assert.False(File.Exists(discovery));
    }

    // ---------------------------------------------------------------- F6 endpoints

    [Fact]
    [Trait("Category", "Integration")] // waits on the SSE poll cycle, not just a request/response
    public async Task GetTranscriptCurrent_StreamsExistingAndNewLinesAsSse()
    {
        using (var log = new TranscriptLog(_transcriptPath))
        {
            log.Append("1", "thinking", "considering the approach");
        }
        var (server, port) = StartServer();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/transcript/current",
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            string? line;
            var saw = false;
            while (!saw && (line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("considering the approach", StringComparison.Ordinal))
                    saw = true;
            }
            Assert.True(saw, "expected the transcript line as an SSE frame within the timeout");
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetProcesses_NoRunDb_ReturnsEmptyList()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/processes");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(0, doc.RootElement.GetProperty("processes").GetArrayLength());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetProcesses_ReturnsTrackedPidsWithLiveness()
    {
        _store.TrackPid(Environment.ProcessId, RunId, "gate:build", "S1", 1, DateTime.UtcNow);
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/processes");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var procs = doc.RootElement.GetProperty("processes");
            Assert.Equal(1, procs.GetArrayLength());
            Assert.Equal(Environment.ProcessId, procs[0].GetProperty("pid").GetInt32());
            Assert.True(procs[0].GetProperty("alive").GetBoolean());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostProcessKill_UntrackedPid_Returns400WithReason()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostJson(port, "/processes/kill", """{"pid":424242}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("not a tracked process", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostProcessKill_MissingPid_Returns400()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostJson(port, "/processes/kill", """{"pid":0}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostProcessKill_TrackedLiveProcess_KillsItAndReturns202()
    {
        using var proc = StartSleepyProcess();
        _store.TrackPid(proc.Id, RunId, "bg:test", "S1", 1, DateTime.UtcNow);
        var (server, port) = StartServer();
        try
        {
            var resp = await PostJson(port, "/processes/kill", $$"""{"pid":{{proc.Id}}}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try { await proc.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            }
            Assert.True(proc.HasExited, "the process should have been killed");

            // A second kill is refused — the pid is now marked exited.
            var again = await PostJson(port, "/processes/kill", $$"""{"pid":{{proc.Id}}}""");
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }
        finally { server.Dispose(); if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
    }

    private static System.Diagnostics.Process StartSleepyProcess()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > NUL")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        System.Threading.Thread.Sleep(200);
        return proc;
    }

    [Fact]
    public async Task GetSessions_NoRunDb_ReturnsEmptyList()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/sessions");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(0, doc.RootElement.GetProperty("sessions").GetArrayLength());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetReportQuery_RejectsNonSelectStatements()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/report/query?sql=DELETE FROM runs");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains("SELECT", doc.RootElement.GetProperty("error").GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetReportQuery_ExecutesSelectAgainstRunDb()
    {
        _store.InitializeRun(RunId, "cps-test", _dir, null, null);
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/report/query?sql=SELECT run_id, plan_name FROM runs");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var rows = doc.RootElement.GetProperty("rows");
            Assert.Equal(1, rows.GetArrayLength());
            var values = rows[0].GetProperty("values");
            Assert.Equal(RunId, values[0].GetString());
            Assert.Equal("cps-test", values[1].GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostInject_MissingContent_Returns400()
    {
        var (server, port) = StartServer();
        try
        {
            using var content = new StringContent("""{"stageId":"S1"}""", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{port}/inject", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostInject_Valid_WritesToRunDbAndReturns202()
    {
        _store.InitializeRun(RunId, "cps-test", _dir, null, null);
        var (server, port) = StartServer();
        try
        {
            using var content = new StringContent("""{"content":"prefer the async path here","stageId":"S1"}""", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{port}/inject", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());

            var rows = _store.Query("SELECT content, target_stage_id FROM injections");
            Assert.Single(rows);
            Assert.Equal("prefer the async path here", rows[0]["content"]);
            Assert.Equal("S1", rows[0]["target_stage_id"]);
        }
        finally { server.Dispose(); }
    }
}
