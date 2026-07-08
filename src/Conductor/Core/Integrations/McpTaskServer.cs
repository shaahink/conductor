using System.Text.Json;
using System.Text.Json.Serialization;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Integrations;

/// <summary>
/// B9.3: minimal MCP (Model Context Protocol) JSON-RPC 2.0 server over stdio.
/// Exposes three tools — task_list, task_update, task_add — that persist the agent's todo list
/// across sessions. Reads existing tasks from the event log; writes new events to a side journal
/// file to avoid concurrent-write races with the conductor's main event log.
/// </summary>
public sealed class McpTaskServer
{
    private readonly string _eventsPath;
    private readonly string _journalPath;
    private readonly string _runId;
    private readonly TaskGraph _graph = new();

    public McpTaskServer(string eventsPath, string journalPath, string runId)
    {
        _eventsPath = eventsPath;
        _journalPath = journalPath;
        _runId = runId;
    }

    /// <summary>Boot the graph from the existing event log.</summary>
    public void Init()
    {
        if (File.Exists(_eventsPath))
        {
            var events = EventLog.ReadAll(_eventsPath);
            _graph.Fold(events);
        }
        // Also fold any MCP journal entries that accumulated between sessions.
        FoldJournal();
    }

    /// <summary>Fold the MCP journal into the graph. Called by Init and after a session
    /// resumes to pick up task changes made by the agent via MCP tools.</summary>
    public void FoldJournal()
    {
        if (File.Exists(_journalPath))
        {
            var events = EventLog.ReadAll(_journalPath);
            _graph.Fold(events);
        }
    }

    /// <summary>
    /// Run the MCP JSON-RPC 2.0 loop over stdio. Reads one JSON-RPC message per line from stdin,
    /// writes one response/notification line to stdout.
    /// </summary>
    public async Task RunAsync(TextReader stdin, TextWriter stdout, CancellationToken ct = default)
    {
        string? line;
        while (!ct.IsCancellationRequested && (line = await stdin.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcResponse? response = null;
            try
            {
                var req = JsonSerializer.Deserialize(line, McpJsonContext.Default.JsonRpcRequest);
                if (req == null) continue;

                response = HandleRequest(req);
            }
            catch (JsonException)
            {
                response = new JsonRpcResponse { Id = null, Error = new JsonRpcError { Code = -32700, Message = "Parse error" } };
            }

            if (response != null)
            {
                var json = JsonSerializer.Serialize(response, McpJsonContext.Default.JsonRpcResponse);
                await stdout.WriteLineAsync(json).ConfigureAwait(false);
                await stdout.FlushAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private JsonRpcResponse? HandleRequest(JsonRpcRequest req)
    {
        // "id" is omitted for notifications — we never respond to those.
        var id = req.Id;
        if (id == null && req.Method != "notifications/initialized")
            return null; // unrecognized notification — ignore

        return req.Method switch
        {
            "initialize" => new JsonRpcResponse
            {
                Id = id,
                Result = JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new {
                        tools = new { listChanged = false }
                    },
                    serverInfo = new { name = "conductor-task-server", version = "1.0.0" }
                }),
            },
            "notifications/initialized" => null, // no response for notification
            "tools/list" => new JsonRpcResponse
            {
                Id = id,
                Result = JsonSerializer.SerializeToElement(new
                {
                    tools = new object[]
                    {
                        new { name = "task_list", description = "List all sub-tasks for the active checkpoint", inputSchema = new { type = "object", properties = new { checkpointId = new { type = "string", description = "The checkpoint id (e.g. B9.3)" } }, required = new[] { "checkpointId" } } },
                        new { name = "task_update", description = "Update a sub-task's status (todo / in_progress / done / skipped)", inputSchema = new { type = "object", properties = new { taskId = new { type = "string" }, status = new { type = "string" } }, required = new[] { "taskId", "status" } } },
                        new { name = "task_add", description = "Add a new sub-task under the active checkpoint", inputSchema = new { type = "object", properties = new { checkpointId = new { type = "string" }, title = new { type = "string" }, order = new { type = "integer" } }, required = new[] { "checkpointId", "title", "order" } } },
                    }
                }),
            },
            "tools/call" => HandleToolCall(id ?? default, req.Params),
            _ => new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {req.Method}" } },
        };
    }

    private JsonRpcResponse HandleToolCall(JsonElement id, JsonElement? @params)
    {
        var name = "unknown";
        JsonElement? args = null;
        if (@params is { } p)
        {
            if (p.TryGetProperty("name", out var n)) name = n.GetString() ?? "unknown";
            if (p.TryGetProperty("arguments", out var a)) args = a;
        }

        try
        {
            var result = name switch
            {
                "task_list" => HandleTaskList(args),
                "task_update" => HandleTaskUpdate(args),
                "task_add" => HandleTaskAdd(args),
                _ => JsonSerializer.SerializeToElement(new { error = $"Unknown tool: {name}" }),
            };
            return new JsonRpcResponse { Id = id, Result = result };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse { Id = id, Result = JsonSerializer.SerializeToElement(new { error = ex.Message }) };
        }
    }

    private JsonElement HandleTaskList(JsonElement? args)
    {
        var cpId = "";
        if (args is { } a && a.TryGetProperty("checkpointId", out var cp))
            cpId = cp.GetString() ?? "";

        var tasks = _graph.ForCheckpoint(cpId);
        var list = tasks.Select(t => new
        {
            taskId = t.TaskId,
            checkpointId = t.CheckpointId,
            title = t.Title,
            status = t.Status,
            source = t.Source,
            order = t.Order,
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { tasks = list, count = list.Length });
    }

    private static readonly HashSet<string> ValidStatuses =
        ["todo", "in_progress", "done", "skipped"];

    private JsonElement HandleTaskUpdate(JsonElement? args)
    {
        var taskId = "";
        var status = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("taskId", out var t)) taskId = t.GetString() ?? "";
            if (a.TryGetProperty("status", out var s)) status = s.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(taskId))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "taskId is required" });
        if (!ValidStatuses.Contains(status))
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"invalid status: '{status}' (must be one of: {string.Join(", ", ValidStatuses)})" });

        var existing = _graph.Find(taskId);
        if (existing == null)
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"task not found: {taskId}" });

        var evt = new TaskStatusChanged
        {
            RunId = _runId,
            TaskId = taskId,
            Status = status,
        };
        WriteJournal(evt);
        _graph.Fold([evt]);

        var actualStatus = _graph.Find(taskId)?.Status ?? existing.Status;
        return JsonSerializer.SerializeToElement(new { ok = true, taskId, status = actualStatus });
    }

    private JsonElement HandleTaskAdd(JsonElement? args)
    {
        var cpId = "";
        var title = "";
        var order = 0;
        if (args is { } a)
        {
            if (a.TryGetProperty("checkpointId", out var c)) cpId = c.GetString() ?? "";
            if (a.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
            if (a.TryGetProperty("order", out var o) && o.TryGetInt32(out var ov)) order = ov;
        }

        if (string.IsNullOrEmpty(cpId))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "checkpointId is required" });
        if (string.IsNullOrWhiteSpace(title))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "title is required" });

        var existing = _graph.ForCheckpoint(cpId);
        var nextOrder = order > 0 ? order : (existing.Count > 0 ? existing.Max(t => t.Order) + 1 : 1);

        var taskId = $"{cpId}-a{nextOrder}";
        var attempt = 0;
        while (_graph.Find(taskId) != null)
        {
            attempt++;
            taskId = $"{cpId}-a{nextOrder}.{attempt}";
        }

        var evt = new TaskAdded
        {
            RunId = _runId,
            TaskId = taskId,
            CheckpointId = cpId,
            Title = title,
            Source = "agent",
            Order = nextOrder,
        };
        WriteJournal(evt);
        _graph.Fold([evt]);

        return JsonSerializer.SerializeToElement(new { ok = true, taskId, checkpointId = cpId, title, order = nextOrder });
    }

    private void WriteJournal(ConductorEvent evt)
    {
        var stamped = evt with
        {
            Ts = DateTime.UtcNow,
            Seq = 0, // journal Seq is rewritten on fold into the main log
        };
        var json = JsonSerializer.Serialize(stamped, EventJsonContext.Default.ConductorEvent);
        var dir = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(_journalPath, json + Environment.NewLine);
    }
}

// ---- JSON-RPC 2.0 message types (source-generated for AOT safety) ----

public sealed class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = "";
    public JsonElement? Id { get; set; }
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonElement? Id { get; set; }
    public JsonElement? Result { get; set; }
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
