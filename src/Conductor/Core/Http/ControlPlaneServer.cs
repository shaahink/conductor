using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Http;

/// <summary>
/// F5: localhost-only HTTP+SSE control plane (design doc §2 AD-1). Read endpoints
/// (<c>GET /state</c>, <c>/tasks</c>, <c>/events</c>) are built entirely from the event log —
/// <see cref="RunStateProjection.Fold"/> + <see cref="SnapshotBuilder.Build"/> for state,
/// <see cref="TaskGraph"/> for tasks — so they never touch <see cref="Orchestrator"/> internals and
/// keep working across future engine refactors. The write endpoint (<c>POST /control</c>) enqueues
/// onto the same inbox the run loop already polls; it never mutates <see cref="RunState"/> on the
/// HTTP request thread, so single-threaded state mutation is preserved exactly as before F5.
/// </summary>
/// <remarks>
/// Uses <see cref="HttpListener"/>, not ASP.NET Core — this is a low-traffic, single-operator,
/// localhost-only surface, and the project has no ASP.NET Core reference today (ratchets against
/// the D12 build-speed goal to add one). Runs its accept loop on a plain background thread, not an
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>, matching <c>ConductorHost</c>'s own
/// "composition + logging root, no long-running hosted service" contract. A bind failure (port in
/// use, no permission) is caught and logged — it is never fatal; headless/no-flag runs must behave
/// identically whether or not this starts (design doc: "headless mode unchanged").
/// </remarks>
public sealed class ControlPlaneServer : IDisposable
{
    private readonly PlanConfig _plan;
    private readonly string _eventsLogPath;
    private readonly string _transcriptLogPath;
    private readonly string _runDbPath;
    private readonly ConcurrentQueue<ControlCommand> _inbox;
    private readonly ILogger _logger;
    private readonly int _preferredPort;
    private HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _acceptThread;
    private volatile bool _running;

    /// <summary>How many consecutive ports to try after the preferred one before giving up. Concurrent
    /// runs (two plans, two terminals) each take the next free port rather than fighting over 4317.</summary>
    private const int PortScanRange = 20;

    /// <summary>The port actually bound — only meaningful once <see cref="Start"/> has returned true.</summary>
    public int Port { get; private set; }
    public bool IsRunning => _running;

    public ControlPlaneServer(PlanConfig plan, string eventsLogPath, string transcriptLogPath, string runDbPath, ConcurrentQueue<ControlCommand> inbox, ILogger logger, int port)
    {
        _plan = plan;
        _eventsLogPath = eventsLogPath;
        _transcriptLogPath = transcriptLogPath;
        _runDbPath = runDbPath;
        _inbox = inbox;
        _logger = logger;
        _preferredPort = port;
        Port = port;
    }

    /// <summary>Binds and starts the accept loop, scanning forward from the preferred port so a second
    /// concurrent run lands on a free one instead of failing. Returns false (never throws) if no port in
    /// the range binds — the run continues headless, exactly as when the control plane is off.</summary>
    public bool Start()
    {
        for (var port = _preferredPort; port < _preferredPort + PortScanRange; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                listener.Close();
                if (port == _preferredPort + PortScanRange - 1)
                {
                    _logger.LogWarning(ex, "control plane: no free port in {Start}-{End} — continuing without it",
                        _preferredPort, _preferredPort + PortScanRange - 1);
                    return false;
                }
                continue; // port taken (most likely by another conductor run) — try the next
            }

            _listener = listener;
            Port = port;
            break;
        }
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "conductor-control-plane" };
        _acceptThread.Start();
        WriteDiscoveryFile();
        _logger.LogInformation("control plane: listening on http://127.0.0.1:{Port}/", Port);
        return true;
    }

    /// <summary>Publishes the bound port to <c>.conductor/control-plane.json</c> so clients (the Face TUI,
    /// a second terminal, <c>conductor chat</c>) can attach to a run by its state dir rather than being told
    /// a port number — which they cannot know once the port is auto-scanned. Best-effort: a failure here
    /// costs discovery, not the run.</summary>
#pragma warning disable MA0045 // Start() is a sync boundary (called from the CLI before the run loop); one small file write needs no async plumbing.
    private void WriteDiscoveryFile()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new ControlPlaneInfo(
                Port, $"http://127.0.0.1:{Port}", Environment.ProcessId, _plan.Name, DateTime.UtcNow),
                ControlPlaneJsonContext.Default.ControlPlaneInfo);
            Directory.CreateDirectory(_plan.StateDir); // the server can start before anything else has touched .conductor/
            File.WriteAllText(DiscoveryPath(_plan.StateDir), payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "control plane: could not write the discovery file");
        }
    }
#pragma warning restore MA0045

    /// <summary>Path of the discovery file for a given state dir. Public so clients resolve it the same way.</summary>
    public static string DiscoveryPath(string stateDir) => Path.Combine(stateDir, "control-plane.json");

#pragma warning disable MA0045 // dedicated background Thread (not the async run loop) — blocking accept here is correct, not sync-over-async
    private void AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext(); // blocks; Stop() makes this throw so the loop exits
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(ctx, _cts.Token), CancellationToken.None);
        }
    }
#pragma warning restore MA0045

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;
            switch (method, path)
            {
                case ("GET", "/state"): await WriteStateAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/tasks"): await WriteTasksAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/events"): await StreamEventsAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/transcript/current"): await StreamTranscriptAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/processes"): await WriteProcessesAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/sessions"): await WriteSessionsAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/report/query"): await WriteQueryAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/control"): await HandleControlPostAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/inject"): await HandleInjectPostAsync(ctx, ct).ConfigureAwait(false); break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "control plane: request handling failed for {Path}", ctx.Request.Url);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    private async Task WriteStateAsync(HttpListenerContext ctx)
    {
        var events = EventLog.ReadAll(_eventsLogPath);
        var runState = RunStateProjection.Fold(events);
        var track = ReadTrackerSafe();
        var snap = SnapshotBuilder.Build(_plan, runState, track);
        var dto = ControlPlaneDto.FromSnapshot(snap, runState.RunId, _plan.Repo, _plan.PlanDir);
        await WriteJsonAsync(ctx, dto, ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false);
    }

    // Mirrors Orchestrator.ReadTrackerSafe: the tracker file may not exist yet (very early in a run,
    // or a dry-run that never wrote one) — that's "no progress yet", not a 500.
    private TrackerSnapshot ReadTrackerSafe()
    {
        try { return ProgressProviderFactory.Create(_plan).Read(_plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    private async Task WriteTasksAsync(HttpListenerContext ctx)
    {
        var events = EventLog.ReadAll(_eventsLogPath);
        var graph = new TaskGraph();
        graph.Fold(events);
        await WriteJsonAsync(ctx, ControlPlaneDto.FromTasks(graph.Tasks), ControlPlaneJsonContext.Default.TasksDto).ConfigureAwait(false);
    }

    /// <summary>Polls events.jsonl for new lines past the last-sent <c>Seq</c> and pushes each as an
    /// SSE <c>data:</c> frame. Polling (not a file watcher) is deliberately simple: this is a
    /// low-traffic single-operator log, and the same JSON contract already used for state/tasks
    /// (<see cref="EventJsonContext"/>) is reused verbatim, so events.jsonl and the live stream never
    /// diverge in shape.</summary>
    private async Task StreamEventsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;
        var lastSeq = ParseSince(ctx);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = EventLog.ReadAll(_eventsLogPath);
                foreach (var evt in events.Where(e => e.Seq > lastSeq).OrderBy(e => e.Seq))
                {
                    var json = JsonSerializer.Serialize<ConductorEvent>(evt, EventJsonContext.Default.ConductorEvent);
                    var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await output.WriteAsync(frame, ct).ConfigureAwait(false);
                    lastSeq = evt.Seq;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
            // client disconnected or the server is shutting down — not an error
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>F6: tails <c>transcript.jsonl</c> (agent text + thinking, one line per <see cref="AgentEvent"/>)
    /// the same way <see cref="StreamEventsAsync"/> tails events.jsonl — the agent pane's only data source
    /// (deliberately deferred out of F5, see design doc F5 stage-map entry). <c>?since=&lt;seq&gt;</c> lets a
    /// reconnecting client resume without re-fetching lines it already has.</summary>
    private async Task StreamTranscriptAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;
        var lastSeq = ParseSince(ctx);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var lines = TranscriptLog.ReadAll(_transcriptLogPath);
                foreach (var line in lines.Where(l => l.Seq > lastSeq).OrderBy(l => l.Seq))
                {
                    var json = JsonSerializer.Serialize(line, TranscriptJsonContext.Default.TranscriptLine);
                    var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await output.WriteAsync(frame, ct).ConfigureAwait(false);
                    lastSeq = line.Seq;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
            // client disconnected or the server is shutting down — not an error
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    private static long ParseSince(HttpListenerContext ctx)
        => long.TryParse(ctx.Request.QueryString["since"], out var since) ? since : 0;

    /// <summary>F6 Process pane (D11): PID rows from run.db + a liveness check + a best-effort last
    /// output line for <c>conductor bg start</c>-launched processes (tailed from their bg-logs file —
    /// gate/agent children have no per-process log today, so this is null for them). Opens a short-
    /// lived RunDb connection per request, same pattern <c>conductor bg status</c> already uses
    /// alongside a running orchestrator (SQLite tolerates the extra reader/writer).</summary>
    private async Task WriteProcessesAsync(HttpListenerContext ctx)
    {
        if (!File.Exists(_runDbPath))
        {
            await WriteJsonAsync(ctx, new ProcessesDto([]), ControlPlaneJsonContext.Default.ProcessesDto).ConfigureAwait(false);
            return;
        }
        var events = EventLog.ReadAll(_eventsLogPath);
        var runId = RunStateProjection.Fold(events).RunId;
        List<PidRow> pids;
        using (var db = new RunDb(_runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance))
        {
            pids = [.. db.GetAllPids(runId)];
        }
        var bgLogDir = Path.Combine(_plan.StateDir, "bg-logs");
        var dtos = new List<ProcessDto>(pids.Count);
        foreach (var p in pids)
        {
            var alive = p.ExitedUtc == null && IsProcessAlive(p.Pid);
            var lastLine = p.Purpose.StartsWith("bg:", StringComparison.Ordinal)
                ? await TailBgLogAsync(bgLogDir, p.Pid).ConfigureAwait(false)
                : null;
            dtos.Add(ControlPlaneDto.FromPid(p, alive, lastLine));
        }
        await WriteJsonAsync(ctx, new ProcessesDto(dtos), ControlPlaneJsonContext.Default.ProcessesDto).ConfigureAwait(false);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<string?> TailBgLogAsync(string bgLogDir, int pid)
    {
        try
        {
            if (!Directory.Exists(bgLogDir)) return null;
            var match = Directory.EnumerateFiles(bgLogDir, $"*-{pid}.log").FirstOrDefault();
            if (match == null) return null;
            var fs = new FileStream(match, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
            await using (fs.ConfigureAwait(false))
            {
                using var reader = new StreamReader(fs);
                string? last = null, current;
                while ((current = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    if (current.Length > 0) last = current;
                return last;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>F6 session-history browser (D11): run.db <c>sessions</c> rows for the current run.</summary>
    private async Task WriteSessionsAsync(HttpListenerContext ctx)
    {
        if (!File.Exists(_runDbPath))
        {
            await WriteJsonAsync(ctx, new SessionsDto([]), ControlPlaneJsonContext.Default.SessionsDto).ConfigureAwait(false);
            return;
        }
        var events = EventLog.ReadAll(_eventsLogPath);
        var runId = RunStateProjection.Fold(events).RunId;
        List<Dictionary<string, object?>> rows;
        using (var db = new RunDb(_runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance))
        {
            rows = db.Query(
                "SELECT number, stage_id, kind, started_utc, ended_utc, outcome, attempt, resume_count, gate_summary, result_summary, commit_count " +
                "FROM sessions WHERE run_id = @runId ORDER BY number DESC", ("@runId", runId));
        }
        var dtos = rows.Select(r => new SessionRowDto(
            Number: Convert.ToInt32(r["number"]),
            StageId: (string)r["stage_id"]!,
            Kind: (string)r["kind"]!,
            StartedUtc: (string)r["started_utc"]!,
            EndedUtc: r["ended_utc"] as string,
            Outcome: r["outcome"] as string,
            Attempt: Convert.ToInt32(r["attempt"]),
            ResumeCount: Convert.ToInt32(r["resume_count"]),
            GateSummary: r["gate_summary"] as string,
            ResultSummary: r["result_summary"] as string,
            CommitCount: Convert.ToInt32(r["commit_count"]))).ToList();
        await WriteJsonAsync(ctx, new SessionsDto(dtos), ControlPlaneJsonContext.Default.SessionsDto).ConfigureAwait(false);
    }

    /// <summary>F6 embedded reporting (D11): the same ad-hoc SQL surface as <c>conductor report --query</c>
    /// (F1.4), SELECT-only — this is a localhost single-operator tool already trusted with destructive
    /// control verbs, but a query textbox typo should never be able to mutate run.db.</summary>
    private async Task WriteQueryAsync(HttpListenerContext ctx)
    {
        var sql = ctx.Request.QueryString["sql"];
        if (string.IsNullOrWhiteSpace(sql))
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, "missing 'sql' query parameter"),
                ControlPlaneJsonContext.Default.QueryResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (!sql.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, "only SELECT queries are allowed"),
                ControlPlaneJsonContext.Default.QueryResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(_runDbPath))
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, "no run.db found"),
                ControlPlaneJsonContext.Default.QueryResultDto).ConfigureAwait(false);
            return;
        }
        const int maxRows = 500;
        try
        {
            List<Dictionary<string, object?>> rows;
            using (var db = new RunDb(_runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance))
            {
                rows = db.Query(sql);
            }
            var columns = rows.Count > 0 ? rows[0].Keys.ToList() : [];
            var truncated = rows.Count > maxRows;
            var dtoRows = rows.Take(maxRows)
                .Select(r => new QueryRowDto([.. columns.Select(c => Convert.ToString(r[c], System.Globalization.CultureInfo.InvariantCulture) ?? "")]))
                .ToList();
            await WriteJsonAsync(ctx, new QueryResultDto(columns, dtoRows, truncated, null),
                ControlPlaneJsonContext.Default.QueryResultDto).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            await WriteJsonAsync(ctx, new QueryResultDto([], [], false, ex.Message),
                ControlPlaneJsonContext.Default.QueryResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
        }
    }

    private async Task HandleControlPostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        ControlCommand cmd;
        try
        {
            cmd = ControlFile.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, new ControlAcceptedDto(false, "malformed JSON body"),
                ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (cmd.Action == null)
        {
            await WriteJsonAsync(ctx, new ControlAcceptedDto(false, "unrecognised or missing 'command'"),
                ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        // Enqueue only — executes on the run loop's own next poll (Orchestrator.PollInbox), never
        // inline on this HTTP thread, so RunState mutation stays single-threaded.
        _inbox.Enqueue(cmd);
        await WriteJsonAsync(ctx, new ControlAcceptedDto(true, null),
            ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    /// <summary>F6 inject editor (D11): records a human injection to run.db's <c>injections</c> table
    /// (same <see cref="RunDb.WriteInjection"/> surface F8's Telegram reply-to-inject will eventually
    /// call) — direct SQLite write, not the control-plane inbox, matching the existing
    /// <c>conductor note</c>/<c>bg start</c> precedent of a short-lived RunDb connection alongside the
    /// running orchestrator. Recorded and acknowledged; NOT YET threaded into the next session's
    /// prompt — see <see cref="InjectRequestDto"/>.</summary>
    private async Task HandleInjectPostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        InjectRequestDto? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.InjectRequestDto);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, new InjectAcceptedDto(false, "malformed JSON body", null, null, null),
                ControlPlaneJsonContext.Default.InjectAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req?.Content))
        {
            await WriteJsonAsync(ctx, new InjectAcceptedDto(false, "missing 'content'", null, null, null),
                ControlPlaneJsonContext.Default.InjectAcceptedDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(_runDbPath))
        {
            await WriteJsonAsync(ctx, new InjectAcceptedDto(false, "no run.db found — run the conductor at least once", null, null, null),
                ControlPlaneJsonContext.Default.InjectAcceptedDto).ConfigureAwait(false);
            return;
        }
        var events = EventLog.ReadAll(_eventsLogPath);
        var runId = RunStateProjection.Fold(events).RunId;
        var recordedUtc = DateTime.UtcNow;
        using (var db = new RunDb(_runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance))
        {
            db.WriteInjection(runId, "human", null, req.StageId, req.Content);
        }
        await WriteJsonAsync(ctx, new InjectAcceptedDto(true, null, runId, req.StageId, recordedUtc.ToString("O")),
            ControlPlaneJsonContext.Default.InjectAcceptedDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext ctx, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        if (!_running) { _cts.Dispose(); return; }
        _running = false;
        _cts.Cancel();
        // Remove the discovery file first: a client that reads it must never be pointed at a dead port.
        try { File.Delete(DiscoveryPath(_plan.StateDir)); } catch (Exception) { /* best effort */ }
        try { _listener.Stop(); } catch (Exception) { /* best effort */ }
        try { _listener.Close(); } catch (Exception) { /* best effort */ }
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
