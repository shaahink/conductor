using Conductor.Core;
using Conductor.Core.Http;
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

namespace Conductor.Http;

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
    private PlanConfig _plan;
    private readonly RunState _state;
    private readonly IRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox;
    private readonly IRunNotifier _telegram;
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

    /// <summary>Per-run write token. Every POST must carry it in <c>X-Conductor-Token</c>; it is
    /// published only through the discovery file, whose filesystem permissions are the trust
    /// boundary. This is what stops a web page from driving the loopback control plane by CSRF
    /// (browsers happily POST to 127.0.0.1, and <c>POST /inject</c> feeds text straight into the
    /// next agent session's prompt — a prompt-injection vector; <c>/plan/edit</c> can plant a gate
    /// shell command). Reads stay open: they're loopback-only and a browser can't read a
    /// cross-origin response without CORS headers, which this server never sends.</summary>
    public string Token { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public ControlPlaneServer(PlanConfig plan, RunState state, IRunStore store, ConcurrentQueue<ControlCommand> inbox,
        IRunNotifier telegram, ILogger logger, int port)
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

    /// <summary>W5.1: adopt the reloaded plan. The run loop swaps the plan into every satellite that
    /// caches one at a session boundary, and this server was not on that list — so after any
    /// <c>/plan/edit</c> the engine and the generated tracker moved on while every Face surface kept
    /// rendering the plan the run started with. Called from the loop's reload boundary only, i.e.
    /// never while a session is running.</summary>
    public void SwapPlan(PlanConfig fresh)
    {
        _plan = fresh;
        // FU-OWNER-13: the swap IS the moment the queued edit stopped being queued. Clearing here
        // rather than on a timer means "reload pending" can never outlive the reload.
        _queuedReloadPlan = null;
        _reloadQueued = false;
    }

    /// <summary>FU-OWNER-13: a plan reload this server queued that the run loop has not applied yet.
    /// <see cref="_queuedReloadPlan"/> is the plan that was actually written to disk, kept because the
    /// only honest answer to "is my telegram block live yet?" needs to know whether the pending edit
    /// carries one — the live <see cref="_plan"/> by design does not know, and re-reading the file on
    /// every status poll would put disk IO on the Face's fastest loop. Null when the queued reload
    /// came from a path with no saved plan in hand (a task edit), in which case the flag alone is
    /// true and no telegram sentence is overridden.</summary>
    private volatile bool _reloadQueued;
    private volatile PlanConfig? _queuedReloadPlan;

    /// <summary>True while an accepted edit is still waiting for the loop's session boundary.</summary>
    internal bool ReloadPending => _reloadQueued;

    /// <summary>Enqueue the reload AND remember that it is outstanding, in one call, so a future
    /// enqueue site cannot add the queue write and forget the flag.</summary>
    private void QueueReload(PlanConfig? saved = null)
    {
        _queuedReloadPlan = saved;
        _reloadQueued = true;
        _inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));
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
                Port, $"http://127.0.0.1:{Port}", Environment.ProcessId, _plan.Name, DateTime.UtcNow, Token),
                ControlPlaneJsonContext.Default.ControlPlaneInfo);
            Directory.CreateDirectory(_plan.StateDir); // the server can start before anything else has touched .conductor/
            File.WriteAllText(ControlPlaneDiscovery.PathFor(_plan.StateDir), payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "control plane: could not write the discovery file");
        }
    }
#pragma warning restore MA0045

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

            // Writes require the per-run token (see Token). 401 before any handler runs, so an
            // unauthorised caller can't reach a deserializer, the advisor, or the event log.
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && !IsAuthorized(ctx))
            {
                await WriteJsonAsync(ctx, new ControlAcceptedDto(false,
                        "missing or invalid X-Conductor-Token — read it from .conductor/control-plane.json"),
                    ControlPlaneJsonContext.Default.ControlAcceptedDto, HttpStatusCode.Unauthorized).ConfigureAwait(false);
                return;
            }

            switch (method, path)
            {
                case ("GET", "/version"): await WriteVersionAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/state"): await WriteStateAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/tasks"): await WriteTasksAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/tasks/update"): await HandleTaskUpdateAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/tasks/add"): await HandleTaskAddAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/tasks/edit"): await HandleTaskEditAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/tasks/refine"): await HandleTaskRefineAsync(ctx, ct).ConfigureAwait(false); break;
                case ("POST", "/tasks/split"): await HandleTaskSplitAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/prompt/blocks"): await WritePromptBlocksAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/events"): await StreamEventsAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/transcript/current"): await StreamTranscriptAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/console/current"): await StreamConsoleAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/processes"): await WriteProcessesAsync(ctx).ConfigureAwait(false); break;
                case ("POST", "/processes/kill"): await HandleProcessKillAsync(ctx, ct).ConfigureAwait(false); break;
                case ("GET", "/sessions"): await WriteSessionsAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/scores"): await WriteScoresAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/prompt/preview"): await WritePromptPreviewAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/timeline"): await WriteTimelineAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/ledger"): await WriteLedgerAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/bugs"): await WriteBugsAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/owner/queue"): await WriteOwnerQueueAsync(ctx).ConfigureAwait(false); break;
                case ("GET", "/evidence"): await WriteEvidenceAsync(ctx).ConfigureAwait(false); break;
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
            BestEffort.Run(() => { ctx.Response.StatusCode = 500; ctx.Response.Close(); }, _logger);
        }
    }

    private bool IsAuthorized(HttpListenerContext ctx)
    {
        var given = ctx.Request.Headers["X-Conductor-Token"];
        if (string.IsNullOrEmpty(given)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token));
    }

    public void Dispose()
    {
        if (!_running) { _cts.Dispose(); return; }
        _running = false;
        _cts.Cancel();
        // Remove the discovery file first: a client that reads it must never be pointed at a dead port.
        BestEffort.Run(() => File.Delete(ControlPlaneDiscovery.PathFor(_plan.StateDir)), _logger);
        BestEffort.Run(() => _listener.Stop(), _logger);
        BestEffort.Run(() => _listener.Close(), _logger);
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
