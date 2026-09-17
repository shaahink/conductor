using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Conductor.Core.Courier;
using Microsoft.Extensions.Logging;

namespace Conductor.Courier;

/// <summary>DV4.3 / findings §6.5 — the courier's end of the loopback seam.
///
/// <para>It lives in the courier's own executable (PK1.1 / D1 moved it out of the engine's) and not in
/// core, because core may not host an HTTP server, and that boundary is a test, not a convention
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
    private readonly ICourierDesk _desk;
    private readonly ILogger _log;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private volatile bool _running;

    /// <summary>The port actually bound. Only meaningful once <see cref="TryStart"/> returned true.</summary>
    public int Port { get; }

    /// <param name="presence">What to answer the hello with — the daemon's own record, read fresh so
    /// the socket and the file can never disagree about what is running.</param>
    /// <param name="desk">What the daemon does with each verb (PK3.1): a push, a send, a reaction, a
    /// delete, the chat list. Returns the ack the caller prints.</param>
    /// <param name="secret">This install's shared secret.</param>
    /// <param name="log">Where refusals go.</param>
    /// <param name="port">The port to bind, or null for <see cref="CourierEndpoint.Port"/>.</param>
    public CourierListener(Func<CourierPresence> presence,
        ICourierDesk desk,
        string secret, ILogger log, int? port = null)
    {
        _presence = presence;
        _desk = desk;
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
                case ("POST", CourierEndpoint.SendPath):
                    await HandleAsync<CourierSend>(ctx, "send", s => s.Protocol, _desk.SendAsync, ct).ConfigureAwait(false);
                    break;
                case ("POST", CourierEndpoint.ReactPath):
                    await HandleAsync<CourierReact>(ctx, "reaction", r => r.Protocol, _desk.ReactAsync, ct).ConfigureAwait(false);
                    break;
                case ("POST", CourierEndpoint.DeletePath):
                    await HandleAsync<CourierDelete>(ctx, "delete", d => d.Protocol, _desk.DeleteAsync, ct).ConfigureAwait(false);
                    break;
                case ("POST", CourierEndpoint.HelloPath):
                    await HandleAsync<CourierHello>(ctx, "hello", h => h.Protocol, _desk.HelloAsync, ct).ConfigureAwait(false);
                    break;
                case ("GET", CourierEndpoint.ChatsPath):
                    await WriteAsync(ctx, HttpStatusCode.OK, _desk.Chats()).ConfigureAwait(false);
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
        var (push, unreadable) = await ReadAsync<CourierPush>(ctx, "push", ct).ConfigureAwait(false);
        if (unreadable) return;

        if (push is null || string.IsNullOrWhiteSpace(push.ChatId))
        {
            await WriteAsync(ctx, HttpStatusCode.BadRequest,
                new CourierAck(false, "a push has to name the chat it is addressed to.")).ConfigureAwait(false);
            return;
        }

        // A protocol-2 push is taken as it always was (PK3.1): the engine that pushes it may be the
        // installed one, and it has not heard of /send.
        if (await RefusedNewerAsync(ctx, push.Protocol).ConfigureAwait(false)) return;

        var ack = await _desk.PushAsync(push, ct).ConfigureAwait(false);
        await WriteAsync(ctx, ack.Accepted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ack)
            .ConfigureAwait(false);
    }

    /// <summary>PK3.1 - one protocol-3 verb: read the body, refuse a newer protocol by name, hand it
    /// to the desk, answer with its ack. The status says accepted or not; the ack says why.</summary>
    private async Task HandleAsync<T>(HttpListenerContext ctx, string what, Func<T, int> protocol,
        Func<T, CancellationToken, Task<CourierAck>> act, CancellationToken ct) where T : class
    {
        var (body, unreadable) = await ReadAsync<T>(ctx, what, ct).ConfigureAwait(false);
        if (unreadable) return;
        if (body is null)
        {
            await WriteAsync(ctx, HttpStatusCode.BadRequest,
                new CourierAck(false, $"the {what} had no body.")).ConfigureAwait(false);
            return;
        }

        if (await RefusedNewerAsync(ctx, protocol(body)).ConfigureAwait(false)) return;

        var ack = await act(body, ct).ConfigureAwait(false);
        await WriteAsync(ctx, ack.Accepted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ack)
            .ConfigureAwait(false);
    }

    /// <summary>The body as <typeparamref name="T"/>. When it cannot be read the 400 is already
    /// written and <c>Unreadable</c> is true.</summary>
    private static async Task<(T? Body, bool Unreadable)> ReadAsync<T>(HttpListenerContext ctx, string what,
        CancellationToken ct) where T : class
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<T>(body, CourierJson.Options), false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            await WriteAsync(ctx, HttpStatusCode.BadRequest,
                new CourierAck(false, $"the {what} was not readable: " + ex.Message)).ConfigureAwait(false);
            return (null, true);
        }
    }

    /// <summary>The version handshake runs in BOTH directions. RefuseStale covers the run refusing an
    /// old courier; this is the other half - a sender from a newer engine, whose request may mean
    /// something this build does not know, is refused by name instead of half-delivered.</summary>
    private static async Task<bool> RefusedNewerAsync(HttpListenerContext ctx, int protocol)
    {
        if (protocol <= CourierProtocol.Version) return false;

        await WriteAsync(ctx, HttpStatusCode.Conflict, new CourierAck(false,
            $"this courier speaks protocol {CourierProtocol.Version.ToString(CultureInfo.InvariantCulture)}; "
          + $"the run speaks {protocol.ToString(CultureInfo.InvariantCulture)}. "
          + "Restart it: " + CourierProtocol.RestartVerb)).ConfigureAwait(false);
        return true;
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
