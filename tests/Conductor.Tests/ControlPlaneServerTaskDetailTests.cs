using Conductor.Http;
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

/// <summary>P3 wire contract: <c>POST /tasks/edit</c> persists structured task data (title/extra
/// context), <c>GET /prompt/blocks?task=</c> serves the labeled composition, and a context edit
/// changes exactly the taskContext block of the recomposed prompt. <c>POST /tasks/refine</c> only
/// proposes — with no advisor configured it is refused with a clear reason.</summary>
public sealed class ControlPlaneServerTaskDetailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-taskdetail-{Guid.NewGuid():N}");
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerTaskDetailTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-taskdetail");

        _plan = new PlanConfig
        {
            Name = "task-detail-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "P3", Title = "Card detail", Sessions = 1, Notes = "Reuse, don't fork." }],
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { /* best effort */ }
    }

    private (ControlPlaneServer server, int port) StartServer()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var state = new RunState { RunId = "run-taskdetail" };
        var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpRunNotifier(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        // server.Port, not the probe port: Start() scans forward when a parallel test fixture
        // grabbed the probed port first, and requests must follow the server, not the probe.
        return (server, server.Port);
    }

    [Fact]
    public async Task PostTasksEdit_PersistsTitleAndContext_AndGetReflectsBoth()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Card detail panel"}""");

            var resp = await PostAsync(port, "/tasks/edit",
                """{"taskId":"P3.1-a1","title":"Card detail panel (Face)","context":"Start from tab_kanban.go"}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("Card detail panel (Face)", doc.RootElement.GetProperty("title").GetString());
            }

            var tasks = await _http.GetStringAsync($"http://127.0.0.1:{port}/tasks");
            using var tdoc = JsonDocument.Parse(tasks);
            var task = Assert.Single(tdoc.RootElement.GetProperty("tasks").EnumerateArray());
            Assert.Equal("Card detail panel (Face)", task.GetProperty("title").GetString());
            Assert.Equal("Start from tab_kanban.go", task.GetProperty("context").GetString());
        }
        finally { server.Dispose(); }
    }

    // PF3: declared paths are card data like context — a paths-only edit is valid, entries are
    // cleaned, and GET /tasks serves them back so the Face and MCP task_list see the claims.
    [Fact]
    public async Task PostTasksEdit_PathsPersist_AndGetTasksServesThem()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Claim some files"}""");
            var resp = await PostAsync(port, "/tasks/edit",
                """{"taskId":"P3.1-a1","paths":[" src/Foo.cs ","","docs/PLAN.md"]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var tasks = await _http.GetStringAsync($"http://127.0.0.1:{port}/tasks");
            using var tdoc = JsonDocument.Parse(tasks);
            var task = Assert.Single(tdoc.RootElement.GetProperty("tasks").EnumerateArray());
            Assert.Equal("Claim some files", task.GetProperty("title").GetString());
            var paths = task.GetProperty("paths").EnumerateArray().Select(p => p.GetString()).ToArray();
            Assert.Equal(new[] { "src/Foo.cs", "docs/PLAN.md" }, paths);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksEdit_ContextOnly_LeavesTheTitleAlone()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Keep me"}""");
            await PostAsync(port, "/tasks/edit", """{"taskId":"P3.1-a1","context":"only this"}""");

            var tasks = await _http.GetStringAsync($"http://127.0.0.1:{port}/tasks");
            using var tdoc = JsonDocument.Parse(tasks);
            var task = Assert.Single(tdoc.RootElement.GetProperty("tasks").EnumerateArray());
            Assert.Equal("Keep me", task.GetProperty("title").GetString());
            Assert.Equal("only this", task.GetProperty("context").GetString());
        }
        finally { server.Dispose(); }
    }

    [Theory]
    [InlineData("""{"taskId":"P3.1-a1","title":"   "}""", "title cannot be blank")]
    [InlineData("""{"taskId":"P3.1-a1"}""", "nothing to edit")]
    [InlineData("""{"taskId":"nope","title":"x"}""", "task not found")]
    [InlineData("""{"title":"x"}""", "taskId is required")]
    public async Task PostTasksEdit_RejectsBadRequests(string body, string expectedError)
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Card"}""");
            var resp = await PostAsync(port, "/tasks/edit", body);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains(expectedError, doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetPromptBlocks_ShowsLabeledBlocks_AndAContextEditChangesExactlyThatBlock()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Compose the blocks"}""");

            var before = await GetBlocksAsync(port, "P3.1-a1");
            Assert.Equal("P3", before.RootElement.GetProperty("stageId").GetString());
            var beforeBlocks = ReadBlocks(before);
            // The checkpoint derives to stage P3, whose notes must appear as a read-only block.
            Assert.Equal("Reuse, don't fork.", beforeBlocks["stageNotes"].Content);
            Assert.False(beforeBlocks["stageNotes"].Editable);
            Assert.Equal("Compose the blocks", beforeBlocks["taskTitle"].Content);
            Assert.True(beforeBlocks["taskTitle"].Editable);
            Assert.Equal("", beforeBlocks["taskContext"].Content);
            Assert.True(beforeBlocks["taskContext"].Editable);

            await PostAsync(port, "/tasks/edit", """{"taskId":"P3.1-a1","context":"owner steer"}""");

            var after = await GetBlocksAsync(port, "P3.1-a1");
            var afterBlocks = ReadBlocks(after);
            Assert.Equal("owner steer", afterBlocks["taskContext"].Content);
            // The wire-level gate: every block except taskContext is byte-identical.
            Assert.Equal(beforeBlocks.Keys, afterBlocks.Keys);
            foreach (var (kind, block) in beforeBlocks)
            {
                if (kind == "taskContext") continue;
                Assert.Equal(block, afterBlocks[kind]);
            }
            before.Dispose();
            after.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetPromptBlocks_UnknownTask_Is404()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{port}/prompt/blocks?task=ghost");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksRefine_WithoutAnAdvisor_IsRefusedWithAClearReason()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Refine me"}""");
            var resp = await PostAsync(port, "/tasks/refine", """{"taskId":"P3.1-a1"}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("no advisor model is configured", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTasksEdit_WithoutToken_Is401()
    {
        var (server, port) = StartServer();
        try
        {
            await PostAsync(port, "/tasks/add", """{"checkpointId":"P3.1","title":"Guarded"}""");
            using var noAuth = new HttpClient();
            using var content = new StringContent("""{"taskId":"P3.1-a1","context":"sneaky"}""", Encoding.UTF8, "application/json");
            var resp = await noAuth.PostAsync($"http://127.0.0.1:{port}/tasks/edit", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    private async Task<JsonDocument> GetBlocksAsync(int port, string taskId)
    {
        var json = await _http.GetStringAsync($"http://127.0.0.1:{port}/prompt/blocks?task={taskId}");
        return JsonDocument.Parse(json);
    }

    private static Dictionary<string, (string Label, string Content, bool Editable)> ReadBlocks(JsonDocument doc)
    {
        var blocks = new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal);
        foreach (var b in doc.RootElement.GetProperty("blocks").EnumerateArray())
        {
            blocks[b.GetProperty("kind").GetString()!] = (
                b.GetProperty("label").GetString()!,
                b.GetProperty("content").GetString()!,
                b.GetProperty("editable").GetBoolean());
        }
        return blocks;
    }

    private async Task<HttpResponseMessage> PostAsync(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }
}
