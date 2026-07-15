using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// B9.3 gate: scripted MCP client calls task_update (and task_list/task_add) and the
/// changes appear in the TaskGraph projection.
/// </summary>
public class McpTaskServerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"mcp-test-{Guid.NewGuid()}.jsonl");

    // MCP is line-delimited JSON-RPC: one request per line, one response per line.
    // All requests in a test are sent in one RunAsync invocation so the server
    // processes them sequentially with in-memory state.
    private static async Task<List<JsonElement>> RunMcpExchange(
        McpTaskServer server, params string[] requests)
    {
        var input = string.Join(Environment.NewLine, requests);
        using var stdin = new StringReader(input);
        await using var stdout = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.RunAsync(stdin, stdout, cts.Token);
        return stdout.ToString()
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonSerializer.Deserialize<JsonElement>(s))
            .ToList();
    }

    private static string Rpc(object payload)
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Serialize(payload, opts);
    }

    [Fact]
    public async Task Initialize_ReturnsServerCapabilities()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-init");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2024-11-05", capabilities = new { } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.Equal("conductor-task-server", result.GetProperty("serverInfo").GetProperty("name").GetString());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task ToolsList_ReturnsAllTools()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-tools");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var tools = responses[0].GetProperty("result").GetProperty("tools");
            Assert.Equal(15, tools.GetArrayLength());
            var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
            Assert.Contains("task_list", names);
            Assert.Contains("task_update", names);
            Assert.Contains("task_add", names);
            Assert.Contains("conductor_note", names);
            Assert.Contains("bg_start", names);
            Assert.Contains("bg_status", names);
            Assert.Contains("bg_logs", names);
            Assert.Contains("bg_stop", names);
            Assert.Contains("run_query", names);
            Assert.Contains("ledger_list", names);
            Assert.Contains("bug_new", names);
            Assert.Contains("bug_list", names);
            Assert.Contains("bug_fix", names);
            Assert.Contains("session_detail", names);
            Assert.Contains("inject_instruction", names);
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task TaskList_ReturnsEmptyForUnknownCheckpoint()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-empty");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_list", arguments = new { checkpointId = "Z99.9" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.Equal(0, result.GetProperty("count").GetInt32());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task TaskAdd_AndTaskUpdate_RoundTripThroughGraph()
    {
        var eventsPath = TempPath();
        var journal = TempPath();
        try
        {
            // Seed events.jsonl with one TaskAdded so we have a known taskId to update
            var added = new TaskAdded { RunId = "r-rt", TaskId = "B9.3-t1", CheckpointId = "B9.3", Title = "Model", Source = "planner", Order = 1 };
            await File.WriteAllTextAsync(eventsPath, JsonSerializer.Serialize(added, EventJsonContext.Default.ConductorEvent) + Environment.NewLine);

            var server = new McpTaskServer(eventsPath, journal, "r-rt");
            server.Init();

            // Send: add new task + update known task + list all in one exchange
            var addReq = Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "B9.3", title = "Tests", order = 2 } } });
            var updateReq = Rpc(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "task_update", arguments = new { taskId = "B9.3-t1", status = "done" } } });
            var listReq = Rpc(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "task_list", arguments = new { checkpointId = "B9.3" } } });

            var responses = await RunMcpExchange(server, addReq, updateReq, listReq);
            Assert.Equal(3, responses.Count);

            // add response
            Assert.True(responses[0].GetProperty("result").GetProperty("ok").GetBoolean());
            // update response
            Assert.True(responses[1].GetProperty("result").GetProperty("ok").GetBoolean());
            // list response
            var tasks = responses[2].GetProperty("result").GetProperty("tasks");
            Assert.Equal(2, tasks.GetArrayLength());
            Assert.Equal("done", tasks[0].GetProperty("status").GetString());    // Model (order 1)
            Assert.Equal("todo", tasks[1].GetProperty("status").GetString());    // Tests (order 2)
        }
        finally
        {
            Cleanup(eventsPath);
            Cleanup(journal);
        }
    }

    [Fact]
    public async Task TaskUpdate_WritesJournal_AndSurvivesReopen()
    {
        var eventsPath = TempPath();
        var journal = TempPath();
        try
        {
            // Seed a task via events.jsonl
            var added = new TaskAdded { RunId = "r-j", TaskId = "B9.3-t1", CheckpointId = "B9.3", Title = "Journal test", Source = "planner", Order = 1 };
            await File.WriteAllTextAsync(eventsPath, JsonSerializer.Serialize(added, EventJsonContext.Default.ConductorEvent) + Environment.NewLine);

            // Server 1: update status → writes journal
            var server1 = new McpTaskServer(eventsPath, journal, "r-j");
            server1.Init();
            var updateReq = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_update", arguments = new { taskId = "B9.3-t1", status = "done" } } });
            await RunMcpExchange(server1, updateReq);

            // Journal should contain one TaskStatusChanged event
            Assert.True(File.Exists(journal));
            var journalEvents = EventLog.ReadAll(journal);
            Assert.Single(journalEvents);
            var jEvt = Assert.IsType<TaskStatusChanged>(journalEvents[0]);
            Assert.Equal("B9.3-t1", jEvt.TaskId);
            Assert.Equal("done", jEvt.Status);

            // Server 2: open fresh, Init calls FoldJournal → picks up the status change
            var server2 = new McpTaskServer(eventsPath, journal, "r-j");
            server2.Init();
            var listReq = Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "task_list", arguments = new { checkpointId = "B9.3" } } });
            var responses = await RunMcpExchange(server2, listReq);
            Assert.Single(responses);
            var tasks = responses[0].GetProperty("result").GetProperty("tasks");
            Assert.Equal(1, tasks.GetArrayLength());
            Assert.Equal("done", tasks[0].GetProperty("status").GetString());
        }
        finally
        {
            Cleanup(eventsPath);
            Cleanup(journal);
        }
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-err");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "nonexistent" } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            Assert.True(responses[0].GetProperty("result").TryGetProperty("error", out _));
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task Notification_NoResponse()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-notify");
            var notif = Rpc(new { jsonrpc = "2.0", method = "notifications/initialized" });
            var list = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            var responses = await RunMcpExchange(server, notif, list);

            // Only the tools/list response should be present (notification got no response)
            Assert.Single(responses);
            Assert.True(responses[0].TryGetProperty("result", out _));
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task TaskUpdate_NonExistentTask_ReturnsError()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-ghost");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_update", arguments = new { taskId = "does-not-exist", status = "done" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("not found", result.GetProperty("error").GetString());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task TaskUpdate_InvalidStatus_ReturnsError()
    {
        var eventsPath = TempPath();
        var journal = TempPath();
        try
        {
            var added = new TaskAdded { RunId = "r1", TaskId = "B9.3-t1", CheckpointId = "B9.3", Title = "Test", Source = "planner", Order = 1 };
            await File.WriteAllTextAsync(eventsPath, JsonSerializer.Serialize(added, EventJsonContext.Default.ConductorEvent) + Environment.NewLine);

            var server = new McpTaskServer(eventsPath, journal, "r1");
            server.Init();
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_update", arguments = new { taskId = "B9.3-t1", status = "bogus" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("invalid status", result.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(eventsPath);
            Cleanup(journal);
        }
    }

    [Fact]
    public async Task TaskAdd_DuplicateOrder_GeneratesUniqueId()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-dup");
            server.Init();

            var add1 = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "B9.3", title = "First", order = 1 } } });
            var add2 = Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "B9.3", title = "Second", order = 1 } } });
            var responses = await RunMcpExchange(server, add1, add2);

            Assert.Equal(2, responses.Count);
            var id1 = responses[0].GetProperty("result").GetProperty("taskId").GetString();
            var id2 = responses[1].GetProperty("result").GetProperty("taskId").GetString();
            Assert.NotEqual(id1, id2);
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task TaskAdd_MissingCheckpoint_ReturnsError()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-add");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "task_add", arguments = new { checkpointId = "", title = "Test" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task RunQuery_Select_Succeeds()
    {
        var runId = "rpu8";
        using var db = CreateTempDb(runId);
        db.RecordSession(runId, "F8", 1, "Deliver",
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc), null, "advanced",
            null, 0, 1, "build pass", null, 0, null);

        // Check direct query
        var direct = db.Query("SELECT COUNT(*) AS cnt FROM sessions");
        Assert.Single(direct);
        Assert.Equal(1L, (long)direct[0]["cnt"]!);

        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, runId, store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "run_query", arguments = new { sql = "SELECT COUNT(*) AS cnt FROM sessions" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(1, result.GetProperty("count").GetInt32());
            var rows = result.GetProperty("rows");
            Assert.Equal(1, rows.GetArrayLength());
            var cntStr = rows[0].GetProperty("cnt").GetString();
            Assert.Equal("1", cntStr);
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task RunQuery_RejectsNonSelect()
    {
        using var db = CreateTempDb();
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "run_query", arguments = new { sql = "DROP TABLE sessions" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("Only SELECT", result.GetProperty("error").GetString());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task RunQuery_RequiresSql()
    {
        using var db = CreateTempDb();
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "run_query", arguments = new { } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("sql is required", result.GetProperty("error").GetString());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task LedgerList_FiltersByStageAndKind()
    {
        using var db = CreateTempDb();
        db.WriteLedger("r-mcp", 1, "F8", "finding", "Something broken");
        db.WriteLedger("r-mcp", 2, "F9", "observation", "All green");
        db.WriteLedger("r-mcp", 3, "F8", "decision", "Triage complete");

        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "ledger_list", arguments = new { stageId = "F8", kind = "finding" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.True(result.GetProperty("ok").GetBoolean());
            var entries = result.GetProperty("entries");
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("F8", entries[0].GetProperty("stageId").GetString());
            Assert.Equal("finding", entries[0].GetProperty("kind").GetString());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task BugTools_NewListFix_RoundTripThroughStore()
    {
        using var db = CreateTempDb("r-mcp");
        var journal = TempPath();
        try
        {
            // file a bug
            var newReq = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "bug_new", arguments = new { title = "cache miss on truth tier", severity = "high", stage_id = "M7" } } });
            var newResp = await RunMcpExchange(new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db), newReq);
            var newResult = newResp[0].GetProperty("result");
            Assert.True(newResult.GetProperty("ok").GetBoolean());
            var id = newResult.GetProperty("id").GetInt64();
            Assert.True(id > 0);

            // list open bugs — the one we filed is there
            var listReq = Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "bug_list", arguments = new { } } });
            var listResp = await RunMcpExchange(new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db), listReq);
            var bugs = listResp[0].GetProperty("result").GetProperty("bugs");
            Assert.Equal(1, bugs.GetArrayLength());
            Assert.Equal("cache miss on truth tier", bugs[0].GetProperty("title").GetString());
            Assert.Equal("open", bugs[0].GetProperty("status").GetString());

            // fix it — then it's no longer open
            var fixReq = Rpc(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "bug_fix", arguments = new { id } } });
            var fixResp = await RunMcpExchange(new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db), fixReq);
            Assert.True(fixResp[0].GetProperty("result").GetProperty("ok").GetBoolean());
            Assert.Empty(db.QueryBugs("r-mcp", "open"));
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task LedgerList_RespectsTail()
    {
        using var db = CreateTempDb("r-mcp");
        for (var i = 0; i < 5; i++)
            db.WriteLedger("r-mcp", i, "F8", "observation", $"Entry {i}");

        // Verify direct write
        var checkRows = db.Query("SELECT COUNT(*) AS cnt FROM ledger WHERE run_id = 'r-mcp'");
        Assert.Equal(5L, (long)checkRows[0]["cnt"]!);

        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "ledger_list", arguments = new { tail = 3 } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(3, result.GetProperty("count").GetInt32());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task SessionDetail_ReturnsSessionWithGates()
    {
        using var db = CreateTempDb();
        db.RecordSession("r-mcp", "F8", 42, "Deliver",
            new DateTime(2026, 7, 11, 2, 4, 43, DateTimeKind.Utc), null, "advanced",
            null, 0, 1, "build pass, tests pass", null, 0, null);
        db.RecordGate("r-mcp", 42, "F8", "build", "fast", "session", null, true, false, false, 0, 6200, null);
        db.RecordGate("r-mcp", 42, "F8", "tests", "full", "session", null, false, false, false, 1, 3100, null);

        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "session_detail", arguments = new { sessionNumber = 42 } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.True(result.GetProperty("ok").GetBoolean());
            var session = result.GetProperty("session");
            Assert.Equal(42, session.GetProperty("number").GetInt32());
            Assert.Equal("Deliver", session.GetProperty("kind").GetString());
            Assert.Equal("advanced", session.GetProperty("outcome").GetString());
            var gates = result.GetProperty("gates");
            Assert.Equal(2, gates.GetArrayLength());
            Assert.True(gates[0].GetProperty("passed").GetBoolean());
            Assert.False(gates[1].GetProperty("passed").GetBoolean());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task SessionDetail_MissingSession_ReturnsError()
    {
        using var db = CreateTempDb();
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp", store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "session_detail", arguments = new { sessionNumber = 999 } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("not found", result.GetProperty("error").GetString());
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task InjectInstruction_WritesToInjectionsTable()
    {
        const string runId = "r-mcp";
        using var db = CreateTempDb(runId);
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, runId, store: db);
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "inject_instruction", arguments = new { content = "Please add more tests", stageId = "F8" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal("F8", result.GetProperty("stageId").GetString());

            // Verify it actually wrote to the injections table
            var rows = db.Query("SELECT content, kind, target_stage_id FROM injections WHERE run_id = @runId",
                ("@runId", runId));
            Assert.Single(rows);
            Assert.Equal("Please add more tests", (string)rows[0]["content"]!);
            Assert.Equal("mcp", (string)rows[0]["kind"]!);
            Assert.Equal("F8", (string)rows[0]["target_stage_id"]!);
        }
        finally { Cleanup(journal); }
    }

    [Fact]
    public async Task InjectInstruction_RequiresContent()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-mcp");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "inject_instruction", arguments = new { stageId = "F8" } } });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var result = responses[0].GetProperty("result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("content is required", result.GetProperty("error").GetString());
        }
        finally { Cleanup(journal); }
    }

    private static SqliteRunStore CreateTempDb(string? runId = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mcp-rundb-{Guid.NewGuid():N}.db");
        var db = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
        db.InitializeRun(runId ?? "r-mcp", "MCP Test", "test", null, "1.0.0");
        return db;
    }

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
