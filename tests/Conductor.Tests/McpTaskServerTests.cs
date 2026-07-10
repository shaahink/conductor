using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Integrations;

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
    public async Task ToolsList_ReturnsFourTools()
    {
        var journal = TempPath();
        try
        {
            var server = new McpTaskServer("nonexistent.jsonl", journal, "r-tools");
            var req = Rpc(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            var responses = await RunMcpExchange(server, req);

            Assert.Single(responses);
            var tools = responses[0].GetProperty("result").GetProperty("tools");
            Assert.Equal(4, tools.GetArrayLength());
            var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
            Assert.Contains("task_list", names);
            Assert.Contains("task_update", names);
            Assert.Contains("task_add", names);
            Assert.Contains("conductor_note", names);
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

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
