using System.Net;
using System.Text;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Http;

using Microsoft.Extensions.Logging;

namespace Conductor.Http;

/// <summary>
/// KS2.2 — the control plane a FINISHED run gets.
///
/// <para>Until now the picker admitted defeat on a past run: "read-only history · conductor history
/// &lt;id&gt;". Every fact the Face renders for a live run — sessions, money, timeline, report — is in
/// that run's <c>run.db</c>, so the missing piece was never the data, it was a socket. This is the
/// socket: the same routes, the same DTOs, served out of <see cref="ArchiveView"/>, with no engine
/// process anywhere.</para>
///
/// <para><b>It is not a second ControlPlaneServer.</b> That type mints a per-run write token in its
/// constructor, publishes a discovery file, and holds a live <c>RunState</c> and the run loop's inbox.
/// An archive has none of those and must be incapable of growing them: there is no token here at all,
/// so <c>api.DataSource.HasWriteToken()</c> is false Face-side and every write affordance hides itself;
/// and every POST is refused with <c>405</c> and the sentence "this run is finished", never the live
/// plane's 401 token hint — a 401 says "find the token and try again", which for a finished run is an
/// instruction that cannot be carried out.</para>
///
/// <para><b>The port is outside the fleet window on purpose.</b> <see cref="Conductor.Core.Fleet.FleetScan"/>
/// probes 4317-4336 and identifies a run by what answers <c>/state</c> there; an archive plane inside
/// that window would appear in <c>conductor ps</c> and in the hub as a live run, which is the precise
/// lie KS1 spent a stage removing. So archive planes bind 4400 upward and write NO discovery file:
/// nothing discovers them, and the only way to one is to have asked for it.</para>
///
/// <para><b>Nothing here writes to the database.</b> Reads go through <see cref="ArchiveView"/> and
/// therefore through <see cref="RunArchive"/>'s <c>Mode=ReadOnly;Cache=Private</c> connection —
/// <see cref="Conductor.Core.Store.SqliteRunStore"/> is never constructed, because its constructor
/// alone would create directories, switch the journal to WAL and migrate the schema of a run from
/// July.</para>
/// </summary>
public sealed class ArchiveControlPlane : IDisposable
{
    /// <summary>First port an archive plane tries. Deliberately clear of
    /// <c>FleetScan.FirstPort</c>..<c>+PortSpan</c> (4317-4336) — see the type remarks.</summary>
    public const int FirstPort = 4400;

    /// <summary>How many consecutive ports to try. Several archives can be open at once (two Faces,
    /// two finished runs) and each takes the next free one rather than fighting.</summary>
    public const int PortScanRange = 20;

    /// <summary>The body every POST gets. It names the state, not a credential: a finished run cannot
    /// be driven by anyone, with any token, so a 401 here would be a lie about what is possible.</summary>
    public const string WriteRefusal = "this run is finished — the archive is read-only, so nothing can be written to it";

    private readonly ArchiveView _view;
    private readonly ILogger _logger;
    private readonly int _preferredPort;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener _listener = new();
    private Thread? _acceptThread;
    private volatile bool _running;

    public ArchiveControlPlane(ArchiveView view, ILogger logger, int port = FirstPort)
    {
        _view = view;
        _logger = logger;
        _preferredPort = port;
        Port = port;
    }

    /// <summary>The port actually bound — meaningful once <see cref="Start"/> has returned true.</summary>
    public int Port { get; private set; }

    public bool IsRunning => _running;

    /// <summary>Where a Face attaches.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The run being served.</summary>
    public ArchiveView View => _view;

    /// <summary>Binds and starts the accept loop, scanning forward from the preferred port. Returns
    /// false (never throws) when nothing in the range binds.</summary>
    public bool Start()
    {
        for (var port = _preferredPort; port < _preferredPort + PortScanRange; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/");
            try
            {
                listener.Start();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                listener.Close();
                if (port == _preferredPort + PortScanRange - 1)
                {
                    _logger.LogWarning(ex, "archive plane: no free port in {Start}-{End}",
                        _preferredPort, _preferredPort + PortScanRange - 1);
                    return false;
                }
                continue;
            }

            _listener = listener;
            Port = port;
            break;
        }

        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "conductor-archive-plane" };
        _acceptThread.Start();
        _logger.LogInformation("archive plane: serving run {RunId} read-only on {Url}", _view.Run.ShortRunId, BaseUrl);
        return true;
    }

#pragma warning disable MA0045 // dedicated background Thread — a blocking accept is correct here
    private void AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
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

            // Every write, refused before any handler runs — the mirror of the live plane's 401 gate,
            // and deliberately a different answer.
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers.Add("Allow", "GET");
                await WriteJsonAsync(ctx, new ControlAcceptedDto(false, WriteRefusal),
                    ControlPlaneJsonContext.Default.ControlAcceptedDto,
                    HttpStatusCode.MethodNotAllowed).ConfigureAwait(false);
                return;
            }

            switch (path)
            {
                case "/version": await WriteJsonAsync(ctx, VersionReport.Current(), VersionJsonContext.Default.VersionReport).ConfigureAwait(false); break;
                case "/state": await WriteJsonAsync(ctx, _view.State(), ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false); break;
                case "/tasks": await WriteJsonAsync(ctx, _view.Tasks(), ControlPlaneJsonContext.Default.TasksDto).ConfigureAwait(false); break;
                case "/sessions": await WriteJsonAsync(ctx, _view.Sessions(), ControlPlaneJsonContext.Default.SessionsDto).ConfigureAwait(false); break;
                case "/timeline": await WriteJsonAsync(ctx, _view.Timeline(), ControlPlaneJsonContext.Default.TimelineDto).ConfigureAwait(false); break;
                case "/ledger": await WriteJsonAsync(ctx, _view.Ledger(), ControlPlaneJsonContext.Default.LedgerDto).ConfigureAwait(false); break;
                case "/bugs": await WriteJsonAsync(ctx, _view.Bugs(ctx.Request.QueryString["status"]), ControlPlaneJsonContext.Default.BugsDto).ConfigureAwait(false); break;
                case "/scores": await WriteJsonAsync(ctx, _view.Scores(), ControlPlaneJsonContext.Default.ScoresDto).ConfigureAwait(false); break;
                case "/plan": await WriteJsonAsync(ctx, _view.Plan(), ControlPlaneJsonContext.Default.PlanDto).ConfigureAwait(false); break;
                case "/evidence":
                    await WriteJsonAsync(ctx, _view.Evidence(ctx.Request.QueryString["checkpoint"], ParseLimit(ctx)),
                        ControlPlaneJsonContext.Default.EvidenceDto).ConfigureAwait(false);
                    break;

                // The Face polls these too. An archive answers them EMPTY rather than 404: a finished
                // run has no child processes, owes the owner nothing, and has no telegram service in
                // this process — and a 404 would make the Face render a connection fault for a section
                // that simply has nothing in it.
                case "/processes": await WriteJsonAsync(ctx, new ProcessesDto([]), ControlPlaneJsonContext.Default.ProcessesDto).ConfigureAwait(false); break;

                // The card's prompt preview is composed from the PLAN FILE, which is in a repo this
                // machine may no longer have — so it is the one read an archive genuinely cannot do.
                // It says that, at 200, in the contract's own `ok:false` shape: a 404 would render as a
                // broken connection, and rebuilding the prompt from today's plan would describe a
                // session that never happened.
                case "/prompt/blocks": await WriteJsonAsync(ctx, ArchivePromptBlocks, ControlPlaneJsonContext.Default.PromptBlocksDto).ConfigureAwait(false); break;

                case "/owner/queue": await WriteJsonAsync(ctx, new OwnerQueueDto(0, DateTime.UtcNow.ToString("O"), []), ControlPlaneJsonContext.Default.OwnerQueueDto).ConfigureAwait(false); break;
                case "/telegram/status": await WriteJsonAsync(ctx, ArchiveTelegramStatus, ControlPlaneJsonContext.Default.TelegramStatusDto).ConfigureAwait(false); break;

                // The raw stream replays the archived log and then holds the connection: a stream that
                // closed would put the Face in a reconnect loop against a run that will never speak
                // again. /transcript and /console have no file to follow, so they hold empty.
                case "/events": await StreamEventsAsync(ctx, ct).ConfigureAwait(false); break;
                case "/transcript/current":
                case "/console/current": await HoldOpenAsync(ctx, ct).ConfigureAwait(false); break;

                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "archive plane: request handling failed for {Path}", ctx.Request.Url);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>Telegram, answered honestly for a run nothing is driving: no service in this process,
    /// nothing will be delivered, and the reason says why rather than reading as "unconfigured".</summary>
    private static readonly TelegramStatusDto ArchiveTelegramStatus = new(
        Configured: false, Started: false, HasToken: false, AllowedChatIds: [],
        PollIntervalSeconds: 0, EnableTwoWay: false, BotUsername: null, LastError: null, LastPollUtc: null,
        WillDeliver: false, WillDeliverReason: "this run is finished — nothing is driving it to notify about",
        RestartRequired: false);

    /// <summary>The prompt preview an archive owes: the reason it has none, in the shape the Face
    /// already knows how to render.</summary>
    private static readonly PromptBlocksDto ArchivePromptBlocks = new(
        Ok: false,
        Error: "this run is finished — its prompts were composed from a plan file the archive does not hold",
        TaskId: "", CheckpointId: "", StageId: "", Blocks: []);

    private static int? ParseLimit(HttpListenerContext ctx)
        => int.TryParse(ctx.Request.QueryString["limit"], out var n) && n > 0 ? n : null;

    /// <summary>Replays the archived event log as SSE, honouring <c>?since=</c>, then holds the socket
    /// open. Nothing new will ever arrive — that is the point of an archive — but the connection stays
    /// so the client is not told the stream failed.</summary>
    private async Task StreamEventsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        var since = long.TryParse(ctx.Request.QueryString["since"], out var s) ? s : 0;
        try
        {
            var output = ctx.Response.OutputStream;
            foreach (var evt in _view.Log().Where(e => e.Seq > since).OrderBy(e => e.Seq))
            {
                var json = JsonSerializer.Serialize<ConductorEvent>(evt, EventJsonContext.Default.ConductorEvent);
                await output.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), ct).ConfigureAwait(false);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            await HoldAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    private static async Task HoldOpenAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        ctx.Response.SendChunked = true;
        try
        {
            await ctx.Response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            await HoldAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* best effort */ }
        }
    }

    private static async Task HoldAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) await Task.Delay(1000, ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext ctx, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
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
