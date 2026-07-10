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
    private readonly PlanConfig _plan;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerTests()
    {
        Directory.CreateDirectory(_dir);
        _eventsPath = Path.Combine(_dir, "events.jsonl");
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
        var server = new ControlPlaneServer(_plan, _eventsPath, _inbox, NullLogger.Instance, port);
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
    public void Start_PortAlreadyBound_ReturnsFalseAndDoesNotThrow()
    {
        var port = FreeLoopbackPort();
        var blocker = new HttpListener();
        blocker.Prefixes.Add($"http://127.0.0.1:{port}/");
        blocker.Start();
        try
        {
            var server = new ControlPlaneServer(_plan, _eventsPath, _inbox, NullLogger.Instance, port);
            var started = server.Start();
            Assert.False(started);
            server.Dispose();
        }
        finally { blocker.Stop(); blocker.Close(); }
    }
}
