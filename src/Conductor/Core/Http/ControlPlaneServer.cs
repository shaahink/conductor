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
    private readonly ConcurrentQueue<ControlCommand> _inbox;
    private readonly ILogger _logger;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _acceptThread;
    private volatile bool _running;

    public int Port { get; }
    public bool IsRunning => _running;

    public ControlPlaneServer(PlanConfig plan, string eventsLogPath, ConcurrentQueue<ControlCommand> inbox, ILogger logger, int port)
    {
        _plan = plan;
        _eventsLogPath = eventsLogPath;
        _inbox = inbox;
        _logger = logger;
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>Binds and starts the accept loop. Returns false (never throws) if the bind fails.</summary>
    public bool Start()
    {
        try
        {
            _listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "control plane: failed to bind 127.0.0.1:{Port} — continuing without it", Port);
            return false;
        }
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "conductor-control-plane" };
        _acceptThread.Start();
        _logger.LogInformation("control plane: listening on http://127.0.0.1:{Port}/", Port);
        return true;
    }

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
                case ("POST", "/control"): await HandleControlPostAsync(ctx, ct).ConfigureAwait(false); break;
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
        await WriteJsonAsync(ctx, ControlPlaneDto.FromSnapshot(snap), ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false);
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
        long lastSeq = 0;
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
        try { _listener.Stop(); } catch (Exception) { /* best effort */ }
        try { _listener.Close(); } catch (Exception) { /* best effort */ }
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
