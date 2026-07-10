using System.Text.Json;
using System.Text.Json.Serialization;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Integrations;

/// <summary>
/// B9.3: minimal MCP (Model Context Protocol) JSON-RPC 2.0 server over stdio.
/// Exposes tools — task_list, task_update, task_add, conductor_note (F1.3) — that persist
/// the agent's progress across sessions. Reads existing tasks from the event log; writes new
/// events to a side journal file to avoid concurrent-write races with the conductor's main
/// event log. The optional <see cref="RunDb"/> parameter enables direct ledger writes for
/// the <c>conductor_note</c> tool (F1.3).
/// </summary>
public sealed class McpTaskServer
{
    private readonly string _eventsPath;
    private readonly string _journalPath;
    private readonly string _runId;
    private readonly TaskGraph _graph = new();
    private readonly RunDb? _runDb;

    public McpTaskServer(string eventsPath, string journalPath, string runId, RunDb? runDb = null)
    {
        _eventsPath = eventsPath;
        _journalPath = journalPath;
        _runId = runId;
        _runDb = runDb;
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
                        new { name = "conductor_note", description = "F1.3: Write a finding/observation to the knowledge ledger. Use this immediately when you discover something important, not at session end.", inputSchema = new { type = "object", properties = new { kind = new { type = "string", description = "Entry kind: finding, observation, trap, decision. Default: note." }, content = new { type = "string", description = "The note text." }, stage_id = new { type = "string", description = "Stage id (e.g. F1). Optional." } }, required = new[] { "content" } } },
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
                "conductor_note" => HandleNote(args),
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

    /// <summary>F1.3: Write a finding/observation to the knowledge ledger.
    /// If run.db is available, writes directly to the ledger table (immediate persistence).
    /// Always emits a <see cref="NoteAdded"/> journal event as a fallback so the note survives
    /// regardless; <see cref="TaskGraph"/> ignores these events by design (notes are not tasks).</summary>
    private JsonElement HandleNote(JsonElement? args)
    {
        var kind = "note";
        var content = "";
        var stageId = "";
        if (args is { } a)
        {
            if (a.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "note";
            if (a.TryGetProperty("content", out var c)) content = c.GetString() ?? "";
            if (a.TryGetProperty("stage_id", out var s)) stageId = s.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(content))
            return JsonSerializer.SerializeToElement(new { ok = false, error = "content is required" });

        // Best-effort direct write to run.db ledger table (F1.3)
        if (_runDb != null)
        {
            try
            {
                _runDb.WriteLedger(_runId, null, string.IsNullOrWhiteSpace(stageId) ? null : stageId, kind, content);
            }
#pragma warning disable CA1031 // catch is best-effort here — journal write is the fallback
            catch { }
#pragma warning restore CA1031
        }

        // Also emit as a journal event so the note appears in events.jsonl projections
        var evt = new NoteAdded
        {
            RunId = _runId,
            Kind = kind,
            Content = content,
            StageId = string.IsNullOrWhiteSpace(stageId) ? null : stageId,
        };
        WriteJournal(evt);

        return JsonSerializer.SerializeToElement(new { ok = true, kind, stageId = string.IsNullOrWhiteSpace(stageId) ? null : stageId });
    }

    private void WriteJournal(ConductorEvent evt)
    {
#pragma warning disable MA0045 // sync journal write is deliberate — called from sync MCP handlers
        var stamped = evt with
        {
            Ts = DateTime.UtcNow,
            Seq = 0, // journal Seq is rewritten on fold into the main log
        };
        var json = JsonSerializer.Serialize(stamped, EventJsonContext.Default.ConductorEvent);
        var dir = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(_journalPath, json + Environment.NewLine);
#pragma warning restore MA0045
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
