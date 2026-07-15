using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Planning;
using Conductor.Core.Store;
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
public sealed partial class ControlPlaneServer : IDisposable
{
    private readonly PlanConfig _plan;
    private readonly RunState _state;
    private readonly IRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox;
    private readonly ITelegramService _telegram;
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

    public ControlPlaneServer(PlanConfig plan, RunState state, IRunStore store, ConcurrentQueue<ControlCommand> inbox,
        ITelegramService telegram, ILogger logger, int port)
    {
        _plan = plan;
        _state = state;
        _store = store;
        _inbox = inbox;
        _telegram = telegram;
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
                case ("GET", "/console/current"): await StreamConsoleAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/processes"): await WriteProcessesAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/processes/kill"): await HandleProcessKillAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/sessions"): await WriteSessionsAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/report/query"): await WriteQueryAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/prompt/preview"): await WritePromptPreviewAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/timeline"): await WriteTimelineAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/ledger"): await WriteLedgerAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/bugs"): await WriteBugsAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/note"): await HandleNotePostAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/bug"): await HandleBugPostAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/bug/resolve"): await HandleBugResolveAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/plan"): await WritePlanAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/plan/edit"): await HandlePlanEditAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/plan/import"): await HandlePlanImportAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/telegram/status"): await WriteTelegramStatusAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/telegram/test"): await HandleTelegramTestAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/telegram/token"): await HandleTelegramTokenAsync(ctx, ct).ConfigureAwait(false); break;
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
