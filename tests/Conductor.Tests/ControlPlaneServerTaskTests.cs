using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>G2.1 wire contract: <c>POST /tasks/add</c> and <c>POST /tasks/update</c> emit the same
/// events the MCP task tools do, land them durably before responding, and <c>GET /tasks</c> reflects
/// the write immediately — the Kanban board's move/add loop, against a real HttpListener.</summary>
public sealed class ControlPlaneServerTaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-tasks-{Guid.NewGuid():N}");
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerTaskTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-tasks");

        _plan = new PlanConfig
        {
            Name = "task-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "G2", Title = "Kanban", Sessions = 1 }],
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private (ControlPlaneServer server, int port) StartServer()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var state = new RunState { RunId = "run-tasks" };
        var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind");
        return (server, port);
    }

    [Fact]
    public async Task PostTasksAdd_ThenGet_ShowsTheCardInTodo()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1","title":"Wire the endpoints"}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("G2.1-a1", doc.RootElement.GetProperty("taskId").GetString());
                Assert.Equal("todo", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal(1, doc.RootElement.GetProperty("order").GetInt32());
            }

            // The gate: an immediate re-fetch (the Face's refresh-after-write) must see the card.
            var tasks = await _http.GetStringAsync($"http://127.0.0.1:{port}/tasks");
            using var tdoc = JsonDocument.Parse(tasks);
            var task = Assert.Single(tdoc.RootElement.GetProperty("tasks").EnumerateArray());
            Assert.Equal("G2.1-a1", task.GetProperty("taskId").GetString());
            Assert.Equal("todo", task.GetProperty("status").GetString());
            Assert.Equal("human", task.GetProperty("source").GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksAdd_SecondCard_GetsNextOrderAndUniqueId()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1","title":"First"}""");
            var resp = await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1","title":"Second"}""");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("G2.1-a2", doc.RootElement.GetProperty("taskId").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("order").GetInt32());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksUpdate_MovesTheCard_AndGetReflectsIt()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1","title":"Move me"}""");

            var resp = await PostAsync(port, "/tasks/update", """{"taskId":"G2.1-a1","status":"done"}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
                Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());

            var tasks = await _http.GetStringAsync($"http://127.0.0.1:{port}/tasks");
            using var tdoc = JsonDocument.Parse(tasks);
            var task = Assert.Single(tdoc.RootElement.GetProperty("tasks").EnumerateArray());
            Assert.Equal("done", task.GetProperty("status").GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksUpdate_ReopensADoneCard()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1","title":"Reopen me"}""");
            await PostAsync(port, "/tasks/update", """{"taskId":"G2.1-a1","status":"done"}""");

            // G2: the Kanban ←-move out of Done — reopening is a legal transition now.
            var resp = await PostAsync(port, "/tasks/update", """{"taskId":"G2.1-a1","status":"in_progress"}""");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("in_progress", doc.RootElement.GetProperty("status").GetString());
        }
        finally { server.Dispose(); }
    }

    [Theory]
    [InlineData("""{"taskId":"G2.1-a1","status":"bogus"}""", "invalid status")]
    [InlineData("""{"taskId":"does-not-exist","status":"done"}""", "task not found")]
    [InlineData("""{"status":"done"}""", "taskId is required")]
    public async Task PostTasksUpdate_RejectsBadRequests(string body, string expectedError)
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1","title":"Card"}""");
            var resp = await PostAsync(port, "/tasks/update", body);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(expectedError, doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksAdd_RejectsMissingTitle()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/tasks/add", """{"checkpointId":"G2.1"}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains("title is required", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    private async Task<HttpResponseMessage> PostAsync(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }
}
