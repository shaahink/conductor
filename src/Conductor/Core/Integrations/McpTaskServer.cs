using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Core.Integrations;

/// <summary>
/// B9.3: minimal MCP (Model Context Protocol) JSON-RPC 2.0 server over stdio.
/// Exposes tools — task_list, task_update, task_add, conductor_note (F1.3), plus
/// bg_start/bg_status/bg_logs/bg_stop (F2.4) — that persist the agent's progress across
/// sessions. Reads existing tasks from the event log; writes new events to a side journal
/// file to avoid concurrent-write races with the conductor's main event log. The optional
/// <see cref="IRunStore"/> parameter enables direct ledger writes for the
/// <c>conductor_note</c> tool (F1.3) and PID tracking for bg tools.
/// </summary>
public sealed partial class McpTaskServer
{
    private readonly string _eventsPath;
    private readonly string _journalPath;
    private readonly string _runId;
    private TaskGraph _graph = new();
    private readonly IRunStore? _store;
    private readonly string? _stateDir;
    private readonly string? _repoPath;
    /// <summary>SC4.1: the conductor session this server was wired for, stamped on every bg child it
    /// starts. Without it the pids row said nothing about who spawned the child, so the battery could
    /// not tell "the session I am about to judge left this running" from "somebody's old server".</summary>
    private readonly int? _sessionNumber;

    /// <summary>W2.2: true when this server writes straight into the run's event log. Then run.db is
    /// the one log both this process and the control plane read and write, so the board is live and
    /// there is a single task-id allocator. Without a store (standalone <c>mcp-serve</c>) the side
    /// journal remains the sink, folded at session end.</summary>
    private bool WritesLive => _store != null;

    public McpTaskServer(string eventsPath, string journalPath, string runId, IRunStore? store = null,
        string? stateDir = null, string? repoPath = null, int? sessionNumber = null)
    {
        _eventsPath = eventsPath;
        _journalPath = journalPath;
        _runId = runId;
        _store = store;
        _stateDir = stateDir;
        _repoPath = repoPath;
        _sessionNumber = sessionNumber;
    }

    /// <summary>Boot the graph from the existing event log.</summary>
    public void Init()
    {
        IReadOnlyList<ConductorEvent> events;
        if (_store != null)
            events = _store.ReadAllEvents(_runId);
        else if (File.Exists(_eventsPath))
            events = EventLog.ReadAll(_eventsPath);
        else
            events = [];
        if (events is { Count: > 0 })
            _graph.Fold(events);
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
            if (events is { Count: > 0 })
                _graph.Fold(events);
        }
    }

    /// <summary>W2.2: rebuild the graph from the authoritative log before every tool call that reads or
    /// writes work items. Two consequences, both required: the agent's <c>task_list</c> sees cards the
    /// owner added from the TUI mid-session, and <see cref="TaskWrites.BuildAdd"/> allocates ids
    /// against every other writer's events instead of against a snapshot taken at process start — the
    /// two-independent-allocators shape whose collisions <see cref="TaskGraph.Fold"/> silently dropped.
    /// A rebuild, not an incremental fold: re-folding events already folded would be first-write-wins
    /// against ourselves. No store means no shared log to re-read, so the journal-fed graph stands.</summary>
    private void RefreshGraph()
    {
        if (!WritesLive) return;
        var fresh = new TaskGraph();
        try { fresh.Fold(_store!.ReadAllEvents(_runId)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return; // keep the last good graph — a transient read failure must not blank the board
        }
        _graph = fresh;
    }

    /// <summary>W2.2: the single sink for task events. Live into run.db when a store is wired (visible
    /// to <c>GET /tasks</c> on the very next poll), else the side journal the session-end fold picks
    /// up. The flush is what makes it *immediately* readable rather than on the drain's 200ms cadence.</summary>
    private void WriteEvent(ConductorEvent evt)
    {
        if (_store is { } store)
        {
            store.AppendEvent(evt);
            store.FlushEvents();
            return;
        }
        WriteJournal(evt);
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

    internal const string DefaultProtocolVersion = "2024-11-05";

    /// <summary>The protocol revision the client asked for, when it named one.</summary>
    private static string? RequestedProtocolVersion(JsonElement? @params)
    {
        if (@params is not { } p || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty("protocolVersion", out var v) || v.ValueKind != JsonValueKind.String) return null;
        var version = v.GetString();
        return string.IsNullOrWhiteSpace(version) ? null : version;
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
                    // W2.1: negotiate rather than dictate. This server's surface is plain tools/call,
                    // valid in every MCP revision, so agreeing to the client's revision is honest and
                    // stops a newer client from being told to speak a 2024 dialect it has moved past.
                    protocolVersion = RequestedProtocolVersion(req.Params) ?? DefaultProtocolVersion,
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
                        new { name = "bg_start", description = "F2.4: Start a long-running background process (>3 min). Captures stdout/stderr to .conductor/bg-logs/ and tracks the PID in run.db. Returns the PID and log file path.", inputSchema = new { type = "object", properties = new { command = new { type = "array", items = new { type = "string" }, description = "Command and arguments as a string array (e.g. ['dotnet', 'run'])." }, purpose = new { type = "string", description = "Human-readable purpose label. Defaults to the command name." }, cwd = new { type = "string", description = "Working directory. Defaults to the plan repo root." } }, required = new[] { "command" } } },
                        new { name = "bg_status", description = "F2.4: List all tracked background processes with their liveness status (running/exited/dead). Reads from the run.db pids table.", inputSchema = new { type = "object", properties = new { } } },
                        new { name = "bg_logs", description = "F2.4: Tail the stdout/stderr log of a background process. Returns the last N lines (default 30).", inputSchema = new { type = "object", properties = new { pid = new { type = "integer", description = "PID of the background process." }, tail = new { type = "integer", description = "Number of lines to return (default 30)." } }, required = new[] { "pid" } } },
                        new { name = "bg_stop", description = "F2.4: Kill a background process by PID (kills entire process tree). Marks the PID as exited in run.db.", inputSchema = new { type = "object", properties = new { pid = new { type = "integer", description = "PID of the background process to kill." } }, required = new[] { "pid" } } },
                        new { name = "run_query", description = "F8.1: Execute an ad-hoc SQL query against the conductor's run.db. Supports SELECT only. Use to answer questions like 'how did s9 die?', 'what are the costs per stage?', 'show me recent gate failures'.", inputSchema = new { type = "object", properties = new { sql = new { type = "string", description = "A SELECT SQL statement to run against run.db." } }, required = new[] { "sql" } } },
                        new { name = "ledger_list", description = "F8.1: List recent knowledge ledger entries (findings, observations, decisions). Returns the last N entries (default 20). Use to recall what was discovered in previous sessions.", inputSchema = new { type = "object", properties = new { stageId = new { type = "string", description = "Filter by stage id (e.g. F7). Optional." }, tail = new { type = "integer", description = "Number of recent entries to return (default 20)." }, kind = new { type = "string", description = "Filter by entry kind (finding, observation, trap, decision). Optional." } } } },
                        new { name = "bug_new", description = "M7.2: File a tracked bug. It becomes a run.db row that OUTLIVES this session, is injected into later prompts, and feeds the audit phase — so a found bug is fixed, not re-discovered. File one when you find a real defect you are not fixing right now.", inputSchema = new { type = "object", properties = new { title = new { type = "string", description = "Short one-line bug title." }, detail = new { type = "string", description = "Longer description / repro. Optional." }, severity = new { type = "string", description = "low | medium | high. Default medium." }, stage_id = new { type = "string", description = "Stage id (e.g. M7). Optional." } }, required = new[] { "title" } } },
                        new { name = "bug_list", description = "M7.2: List tracked bugs. Defaults to OPEN bugs; pass status='all' for every bug. Check this before hunting for bugs — the open ones are already known, do not re-file them.", inputSchema = new { type = "object", properties = new { status = new { type = "string", description = "open (default) | fixed | wontfix | all." } } } },
                        new { name = "bug_fix", description = "M7.2: Close a tracked bug once genuinely resolved (or wontfix). Records this session as the one that closed it.", inputSchema = new { type = "object", properties = new { id = new { type = "integer", description = "Bug id (from bug_list)." }, wontfix = new { type = "boolean", description = "Close as wontfix instead of fixed. Optional." } }, required = new[] { "id" } } },
                        new { name = "session_detail", description = "F8.1: Get detailed info about a specific session — outcome, gates run, cost, tokens, commits, result summary.", inputSchema = new { type = "object", properties = new { sessionNumber = new { type = "integer", description = "Session number to look up." }, stageId = new { type = "string", description = "Stage id to scope to. Optional." } }, required = new[] { "sessionNumber" } } },
                        new { name = "inject_instruction", description = "F8.1: Write an instruction into the conductor's injection queue. The next session will receive this as an injected instruction in its prompt. Use to update tasks, inject context, or redirect work.", inputSchema = new { type = "object", properties = new { content = new { type = "string", description = "The instruction text to inject into the next session prompt." }, stageId = new { type = "string", description = "Target stage id (e.g. F7). Optional — injects into current stage by default." } }, required = new[] { "content" } } },
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
            var payload = name switch
            {
                "task_list" => HandleTaskList(args),
                "task_update" => HandleTaskUpdate(args),
                "task_add" => HandleTaskAdd(args),
                "conductor_note" => HandleNote(args),
                "bg_start" => HandleBgStart(args),
                "bg_status" => HandleBgStatus(args),
                "bg_logs" => HandleBgLogs(args),
                "bg_stop" => HandleBgStop(args),
                "run_query" => HandleRunQuery(args),
                "ledger_list" => HandleLedgerList(args),
                "bug_new" => HandleBugNew(args),
                "bug_list" => HandleBugList(args),
                "bug_fix" => HandleBugFix(args),
                "session_detail" => HandleSessionDetail(args),
                "inject_instruction" => HandleInjectInstruction(args),
                _ => JsonSerializer.SerializeToElement(new { error = $"Unknown tool: {name}" }),
            };
            return new JsonRpcResponse { Id = id, Result = ToolResult(payload) };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Result = ToolResult(JsonSerializer.SerializeToElement(new { ok = false, error = ex.Message })),
            };
        }
    }

    /// <summary>
    /// W2.1: wrap a tool's payload in the MCP <c>tools/call</c> result envelope —
    /// <c>{content:[{type:"text",text:…}], isError:…}</c>.
    /// </summary>
    /// <remarks>
    /// Returning the bare payload is what made every conductor tool useless on the real provider: the
    /// call SUCCEEDED, and the agent was shown "(mcp__conductor-tasks__task_list completed with no
    /// output)", because a client renders <c>content</c> and there was none. The verbs looked wired
    /// from the engine side and answered nothing from the agent's side. The payload itself is
    /// unchanged, carried as JSON text, so a model still gets structured data to read.
    /// </remarks>
    private static JsonElement ToolResult(JsonElement payload)
    {
        var isError = payload.ValueKind == JsonValueKind.Object
            && ((payload.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                || payload.TryGetProperty("error", out _));
        return JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = payload.GetRawText() } },
            isError,
        });
    }
}


