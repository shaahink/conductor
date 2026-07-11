using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F5 curl-level contract tests (design doc's own stated gate for the control plane): a real
/// HttpListener bound to an ephemeral loopback port, exercised with real HTTP requests — no mocking
/// of the transport. Covers the read side (state/tasks built from a fixture events.jsonl, matching
/// what RunStateProjection/TaskGraph/SnapshotBuilder already produce elsewhere) and the write side
/// (POST /control enqueues onto the same inbox Orchestrator.PollInbox drains).
/// </summary>
public sealed class ControlPlaneServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-cps-{Guid.NewGuid():N}");
    private readonly string _eventsPath;
    private readonly string _transcriptPath;
    private readonly string _runDbPath;
    private readonly PlanConfig _plan;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerTests()
    {
        Directory.CreateDirectory(_dir);
        _eventsPath = Path.Combine(_dir, "events.jsonl");
        _transcriptPath = Path.Combine(_dir, "transcript.jsonl");
        _runDbPath = Path.Combine(_dir, "run.db");
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
        using var log = new EventLog(_eventsPath, "run-cps");
        foreach (var e in events) log.Emit(e);
    }

    private (ControlPlaneServer server, int port) StartServer()
    {
        var port = FreeLoopbackPort();
        var server = new ControlPlaneServer(_plan, _eventsPath, _transcriptPath, _runDbPath, _inbox, NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind — cannot run contract tests");
        return (server, port);
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
            var server = new ControlPlaneServer(_plan, _eventsPath, _transcriptPath, _runDbPath, _inbox, NullLogger.Instance, port);
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
        var server = new ControlPlaneServer(_plan, _eventsPath, _transcriptPath, _runDbPath, _inbox, NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start());

        var discovery = ControlPlaneServer.DiscoveryPath(_plan.StateDir);
        Assert.True(File.Exists(discovery));

        var info = JsonSerializer.Deserialize(File.ReadAllText(discovery), ControlPlaneJsonContext.Default.ControlPlaneInfo);
        Assert.NotNull(info);
        Assert.Equal(server.Port, info!.Port);
        Assert.Equal($"http://127.0.0.1:{server.Port}", info.BaseUrl);

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
        WriteEvents(new RunStarted { Plan = "cps-test", Repo = _dir });
        using (var db = new RunDb(_runDbPath, NullLogger<RunDb>.Instance))
        {
            db.TrackPid(Environment.ProcessId, "run-cps", "gate:build", "S1", 1, DateTime.UtcNow);
        }
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
        WriteEvents(new RunStarted { Plan = "cps-test", Repo = _dir });
        using (var db = new RunDb(_runDbPath, NullLogger<RunDb>.Instance))
        {
            db.InitializeRun("run-cps", "cps-test", _dir, null, null);
        }
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/report/query?sql=SELECT run_id, plan_name FROM runs");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var rows = doc.RootElement.GetProperty("rows");
            Assert.Equal(1, rows.GetArrayLength());
            var values = rows[0].GetProperty("values");
            Assert.Equal("run-cps", values[0].GetString());
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
        WriteEvents(new RunStarted { Plan = "cps-test", Repo = _dir });
        using (var db = new RunDb(_runDbPath, NullLogger<RunDb>.Instance))
        {
            db.InitializeRun("run-cps", "cps-test", _dir, null, null);
        }
        var (server, port) = StartServer();
        try
        {
            using var content = new StringContent("""{"content":"prefer the async path here","stageId":"S1"}""", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{port}/inject", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());

            using var db = new RunDb(_runDbPath, NullLogger<RunDb>.Instance);
            var rows = db.Query("SELECT content, target_stage_id FROM injections");
            Assert.Single(rows);
            Assert.Equal("prefer the async path here", rows[0]["content"]);
            Assert.Equal("S1", rows[0]["target_stage_id"]);
        }
        finally { server.Dispose(); }
    }
}
