using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Courier;

/// <summary>DV4.3 / findings §6.5 — the run's end of the loopback seam.
///
/// <para>Three refusals before a byte is sent, all of them by name: no courier running, a courier
/// with no listener (protocol 1), and a courier older than this run (<see
/// cref="CourierProtocol.RefuseStale"/>, reused unchanged — the record it reads over the socket is
/// the record DV4.2 already writes to disk, so there is one definition of "stale" and not two).</para>
///
/// <para>The secret goes in a header on every request including the hello. A hello that answered
/// without auth would tell any local process which engine version is running and which scheduled
/// task to attack, and "it is only a version string" is how a fingerprinting endpoint gets
/// built.</para></summary>
public sealed class CourierClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _secret;

    /// <summary>The port this client dials — what the presence record said, not the constant.</summary>
    public int Port { get; }

    /// <param name="port">The port the running courier bound.</param>
    /// <param name="secret">This install's shared secret.</param>
    /// <param name="http">A client to borrow, for a rig. When null one is made and disposed here.</param>
    public CourierClient(int port, string secret, HttpClient? http = null)
    {
        Port = port;
        _secret = secret ?? "";
        _ownsHttp = http is null;
        // Short by design: this is a loopback hop to a process on the same machine. A run blocking
        // for a minute on a daemon that is wedged is the single-point-of-failure §1.4-B warns about,
        // wearing the clothes of a healthy channel.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>A client for the courier running on this machine, or null with the reason. The
    /// reason is a sentence a surface prints verbatim — see the type remarks for the three.</summary>
    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    /// <param name="refusal">Why there is no client, or null when there is one.</param>
    /// <param name="probe">Pid liveness, for a rig. See <see cref="CourierPresence.Live"/>.</param>
    /// <param name="http">A client to send on (PK3.2: <c>say</c> uploads files through the courier and
    /// needs longer than the run's ten seconds). Null makes one.</param>
    public static CourierClient? TryOpen(string? stateHomeRoot, out string? refusal,
        Func<int, DateTimeOffset?>? probe = null, HttpClient? http = null)
    {
        var live = CourierPresence.Live(stateHomeRoot, probe);
        refusal = CourierEndpoint.Unreachable(live) ?? CourierProtocol.RefuseStale(live);
        if (refusal is not null) return null;

        var secret = CourierSecret.Read(stateHomeRoot);
        if (secret is not { Length: > 0 })
        {
            refusal = "the courier is running but this install has no shared secret at "
                    + CourierHome.SecretPathFor(stateHomeRoot)
                    + ", so a run cannot prove who it is. Restart it: " + CourierProtocol.RestartVerb;
            return null;
        }

        return new CourierClient(live!.Port!.Value, secret, http);
    }

    /// <summary>What the daemon says it is, asked over the socket. Null when it did not answer —
    /// which is the courier-down case DV1.1's channel health turns into a loud line.</summary>
    public async Task<CourierPresence?> HelloAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = Request(HttpMethod.Get, CourierEndpoint.HelloPath);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CourierPresence>(body, CourierJson.Options);
        }
        catch (Exception ex) when (Transport(ex, ct))
        {
            return null;
        }
    }

    /// <summary>Hands one message to the daemon. Never throws: every caller on the
    /// <c>IMessageChannel</c> seam is fire-and-forget by contract, so a dead daemon has to come back
    /// as a refusal with a sentence, not as an exception crossing the seam.</summary>
    public Task<CourierAck> PushAsync(CourierPush push, CancellationToken ct = default) =>
        PostAsync(CourierEndpoint.PushPath, push, ct);

    /// <summary>PK3.2 / D5 - one protocol-3 send. The ack carries the message ids.</summary>
    public Task<CourierAck> SendAsync(CourierSend send, CancellationToken ct = default) =>
        PostAsync(CourierEndpoint.SendPath, send, ct);

    /// <summary>PK3.2 / D5 - a reaction by message id.</summary>
    public Task<CourierAck> ReactAsync(CourierReact react, CancellationToken ct = default) =>
        PostAsync(CourierEndpoint.ReactPath, react, ct);

    /// <summary>PK3.2 / D5 - a delete by message id.</summary>
    public Task<CourierAck> DeleteAsync(CourierDelete delete, CancellationToken ct = default) =>
        PostAsync(CourierEndpoint.DeletePath, delete, ct);

    /// <summary>One POST, answered as an ack whatever happened. <see cref="CourierAck.Unanswered"/> is
    /// set only when the connection itself failed - see its remarks for why a timeout is not that.</summary>
    private async Task<CourierAck> PostAsync<T>(string path, T payload, CancellationToken ct)
    {
        try
        {
            using var req = Request(HttpMethod.Post, path);
            req.Content = JsonContent.Create(payload, options: CourierJson.Options);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return new CourierAck(false, "the courier refused this run's shared secret; it is at "
                    + CourierHome.SecretPathFor() + ". Restart it: " + CourierProtocol.RestartVerb);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var ack = Read(body);
            if (ack is not null) return ack;

            return new CourierAck(false, $"the courier answered {(int)resp.StatusCode} with nothing this run could read.");
        }
        catch (Exception ex) when (Transport(ex, ct))
        {
            return new CourierAck(false, $"the courier on port {Port.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + $"did not answer ({ex.Message}). Restart it: " + CourierProtocol.RestartVerb)
            {
                Unanswered = ex is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError },
            };
        }
    }

    private static CourierAck? Read(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<CourierAck>(body, CourierJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, CourierEndpoint.BaseUrl(Port) + path);
        req.Headers.TryAddWithoutValidation(CourierEndpoint.AuthHeader, _secret);
        return req;
    }

    private static bool Transport(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
        && !ct.IsCancellationRequested;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
