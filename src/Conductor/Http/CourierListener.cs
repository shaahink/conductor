using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Conductor.Core.Courier;
using Microsoft.Extensions.Logging;

namespace Conductor.Http;

/// <summary>DV4.3 / findings §6.5 — the courier's end of the loopback seam.
///
/// <para>It lives in this assembly and not in core for the reason the project file states outright:
/// core may not host an HTTP server, and that boundary is a test, not a convention
/// (<c>ArchitectureBoundaryTests</c>). The wire CONTRACT — <see cref="CourierPush"/>,
/// <see cref="CourierAck"/>, <see cref="CourierPresence"/> — is core, because the run's client end
/// speaks it and the run's client end is core.</para>
///
/// <para><b>Two rules from ADR-0005, restated because the port is new.</b> The prefix is literal
/// loopback, so nothing off this machine can reach it at all. And every request — including the
/// hello — carries this install's shared secret, so nothing ON this machine can push to the owner's
/// chat as the run, or fingerprint which engine is answering the phone.</para>
///
/// <para><b>It does not scan.</b> There is exactly one courier per machine by construction, so a
/// port already in use is a second courier or something impersonating one, and the answer is a
/// refusal that names the port and the verb — never the next port up, which is how a run comes to
/// hand its pushes to a stranger.</para></summary>
public sealed class CourierListener : IDisposable
{
    private readonly Func<CourierPresence> _presence;
    private readonly Func<CourierPush, CancellationToken, Task<CourierAck>> _onPush;
    private readonly ILogger _log;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private volatile bool _running;

    /// <summary>The port actually bound. Only meaningful once <see cref="TryStart"/> returned true.</summary>
    public int Port { get; }

    /// <param name="presence">What to answer the hello with — the daemon's own record, read fresh so
    /// the socket and the file can never disagree about what is running.</param>
    /// <param name="onPush">What the daemon does with a push. Returns the ack the run prints.</param>
    /// <param name="secret">This install's shared secret.</param>
    /// <param name="log">Where refusals go.</param>
    /// <param name="port">The port to bind, or null for <see cref="CourierEndpoint.Port"/>.</param>
    public CourierListener(Func<CourierPresence> presence,
        Func<CourierPush, CancellationToken, Task<CourierAck>> onPush,
        string secret, ILogger log, int? port = null)
    {
        _presence = presence;
        _onPush = onPush;
        _secret = secret;
        _log = log;
        Port = port ?? CourierEndpoint.Port;
    }

    /// <summary>Binds and starts accepting. False with a named reason rather than an exception: a
    /// courier that cannot open its socket must still poll the phone, because inbound notes are the
    /// half of this daemon that works with no run alive at all.</summary>
    public bool TryStart(out string? refusal)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(CourierEndpoint.PrefixFor(Port));
        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            listener.Close();
            refusal = $"port {Port.ToString(CultureInfo.InvariantCulture)} is already in use ({ex.Message}), "
                    + "so runs on this machine cannot push through this courier. Another courier is "
                    + "probably already running: check \"conductor courier status\", or point this one "
                    + $"somewhere else with {CourierEndpoint.PortEnvVar}.";
            return false;
        }

        _listener = listener;
        _running = true;
        var accept = new Thread(AcceptLoop) { IsBackground = true, Name = "conductor-courier" };
        accept.Start();
        refusal = null;
        return true;
    }

#pragma warning disable MA0045 // dedicated background Thread, not the async loop: a blocking accept here is correct.
    private void AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener!.GetContext();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // stopped
            }

            _ = Task.Run(() => HandleAsync(ctx, _cts.Token), CancellationToken.None);
        }
    }
#pragma warning restore MA0045

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            // Auth first, on EVERY verb. See the type remarks for why the hello is not exempt.
            if (!CourierSecret.Matches(ctx.Request.Headers[CourierEndpoint.AuthHeader], _secret))
            {
                await WriteAsync(ctx, HttpStatusCode.Unauthorized,
                    new CourierAck(false, $"missing or invalid {CourierEndpoint.AuthHeader} - read it from "
                        + CourierHome.SecretPathFor())).ConfigureAwait(false);
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            switch (ctx.Request.HttpMethod, path)
            {
                case ("GET", CourierEndpoint.HelloPath):
                    await WriteAsync(ctx, HttpStatusCode.OK, _presence()).ConfigureAwait(false);
                    break;
                case ("POST", CourierEndpoint.PushPath):
                    await HandlePushAsync(ctx, ct).ConfigureAwait(false);
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "courier listener: {Path} failed", ctx.Request.Url);
            Close(ctx);
        }
    }

    private async Task HandlePushAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        CourierPush? push;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            push = JsonSerializer.Deserialize<CourierPush>(body, CourierJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            await WriteAsync(ctx, HttpStatusCode.BadRequest,
                new CourierAck(false, "the push was not readable: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        if (push is null || string.IsNullOrWhiteSpace(push.ChatId))
        {
            await WriteAsync(ctx, HttpStatusCode.BadRequest,
                new CourierAck(false, "a push has to name the chat it is addressed to.")).ConfigureAwait(false);
            return;
        }

        // The version handshake runs in BOTH directions. RefuseStale covers the run refusing an old
        // courier; this is the other half — a run from a newer engine, whose push may mean something
        // this build does not know, is refused by name instead of half-delivered.
        if (push.Protocol > CourierProtocol.Version)
        {
            await WriteAsync(ctx, HttpStatusCode.Conflict, new CourierAck(false,
                $"this courier speaks protocol {CourierProtocol.Version.ToString(CultureInfo.InvariantCulture)}; "
              + $"the run speaks {push.Protocol.ToString(CultureInfo.InvariantCulture)}. "
              + "Restart it: " + CourierProtocol.RestartVerb)).ConfigureAwait(false);
            return;
        }

        var ack = await _onPush(push, ct).ConfigureAwait(false);
        await WriteAsync(ctx, ack.Accepted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ack)
            .ConfigureAwait(false);
    }

    private static async Task WriteAsync<T>(HttpListenerContext ctx, HttpStatusCode status, T payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CourierJson.Options));
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static void Close(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The caller hung up first; there is nothing left to answer.
        }
    }

    public void Dispose()
    {
        _running = false;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        Quietly(() => _listener?.Stop());
        Quietly(() => _listener?.Close());
        _cts.Dispose();
    }

    private static void Quietly(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException)
        {
            // Shutdown races with the accept loop by design.
        }
    }
}
