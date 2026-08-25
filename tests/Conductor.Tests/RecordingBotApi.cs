using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Conductor.Tests;

/// <summary>One Bot API call as it arrived on the wire — JSON or multipart. The existing stubs
/// (<c>FakeBotApi</c>, in the FU-OWNER-11 and K5.2 suites) record only the <c>text</c> field of a
/// <c>sendMessage</c>, which cannot answer any of K5.4's questions: whether a push buzzed, which
/// thread it belongs to, or whether a PNG was UPLOADED rather than named.</summary>
/// <param name="ChatId">KS11.3: WHICH chat the call was addressed to. Deliberately absent from
/// <see cref="BotCall.Describe"/> and therefore from every golden — a per-profile test needs the
/// field, and the goldens do not need to move to give it one.</param>
public sealed record BotCall(
    string Method,
    string? Text,
    string? Caption,
    bool DisableNotification,
    long? ReplyToMessageId,
    bool AllowSendingWithoutReply,
    long? MessageThreadId,
    string? FileField,
    string? FileName,
    long FileBytes,
    string? ChatId = null)
{
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Method);
        if (DisableNotification) sb.Append(" [silent]"); else sb.Append(" [notify]");
        if (MessageThreadId is { } t) sb.Append(CultureInfo.InvariantCulture, $" [topic {t}]");
        if (ReplyToMessageId is { } r) sb.Append(CultureInfo.InvariantCulture, $" [reply to {r}]");
        if (FileField is { } f) sb.Append(CultureInfo.InvariantCulture, $" [{f}={FileName}, {FileBytes} B]");
        sb.Append('\n').Append("    ").Append((Text ?? Caption ?? "").Replace("\n", "\n    ", StringComparison.Ordinal));
        return sb.ToString();
    }
}

/// <summary>Stands in for api.telegram.org and records what the engine actually POSTed — every
/// method, every field, and for an upload the field name, file name and byte count. Never records
/// the URL: the path carries the bot token, which on a developer machine may be the real one.</summary>
public sealed class RecordingBotApi : IDisposable
{
    /// <summary>The <c>message_id</c> this stub hands back for every send — the value the engine
    /// should thread subsequent messages onto.</summary>
    public const long AssignedMessageId = 4242;

    private readonly HttpListener _listener = new();
    private readonly Lock _gate = new();
    private readonly List<BotCall> _calls = new();

    public string Root { get; }

    public RecordingBotApi()
    {
        var port = FreePort();
        Root = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        _listener.Prefixes.Add(Root + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public List<BotCall> Snapshot()
    {
        lock (_gate) return new List<BotCall>(_calls);
    }

    /// <summary>KS11.1: the stub can now DELIVER as well as record. Every inbound-command test in
    /// this repo used to stand up its own listener because this one answered <c>getUpdates</c> with
    /// a hard-coded empty list; the seam's surface — every command, on every profile — has to be
    /// driveable through the same double that captures what came back, or the two halves of one
    /// exchange are asserted against two different fakes.</summary>
    public void QueueCommand(string chatId, string text)
    {
        lock (_gate) _pending.Enqueue((chatId, text));
    }

    private readonly Queue<(string ChatId, string Text)> _pending = new();
    private int _nextUpdateId = 1;

    /// <summary>DV2.3, bug #38: make this stub behave like a Bot API that already has another
    /// consumer on the token — every <c>getUpdates</c> answered <c>409 Conflict</c> with this body,
    /// verbatim, until it is set back to null. Off by default, so no existing test moves.</summary>
    public string? ConflictBody { get; set; }

    /// <summary>How many <c>getUpdates</c> polls this stub has answered. A backoff test needs to
    /// know the loop really came back, not just that it logged once.</summary>
    public int PollCount { get { lock (_gate) return _polls; } }

    private int _polls;

    /// <summary>The username <c>getMe</c> reports. The generic success body this stub used to return
    /// for every method parses as a getMe with a NULL username, which is not what the real API does
    /// and hides whether the engine read the field at all.</summary>
    public string BotUsername { get; set; } = "dv23_stub_bot";

    public async Task<bool> WaitForPollsAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (PollCount >= count) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Hands over every queued command ONCE. A long-poll that kept re-serving the same
    /// update would have the engine answer it on every tick, which is a livelock rather than a
    /// test.</summary>
    private string DrainUpdates()
    {
        (string ChatId, string Text)[] batch;
        int first;
        lock (_gate)
        {
            if (_pending.Count == 0) return """{"ok":true,"result":[]}""";
            batch = _pending.ToArray();
            _pending.Clear();
            first = _nextUpdateId;
            _nextUpdateId += batch.Length;
        }

        var sb = new StringBuilder("""{"ok":true,"result":[""");
        for (var i = 0; i < batch.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var id = (first + i).ToString(CultureInfo.InvariantCulture);
            sb.Append("{\"update_id\":").Append(id)
              .Append(",\"message\":{\"message_id\":").Append(id)
              .Append(",\"chat\":{\"id\":").Append(batch[i].ChatId)
              .Append("},\"text\":").Append(JsonSerializer.Serialize(batch[i].Text))
              .Append("}}");
        }
        return sb.Append("]}").ToString();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; }   // listener stopped — that is the exit condition

            var method = (ctx.Request.Url?.AbsolutePath ?? "").Split('/')[^1];
            var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);

            if (string.Equals(method, "getUpdates", StringComparison.Ordinal))
            {
                lock (_gate) _polls++;
                var conflict = ConflictBody;
                if (conflict != null)
                {
                    await RespondAsync(ctx, conflict, HttpStatusCode.Conflict).ConfigureAwait(false);
                    continue;
                }
                await RespondAsync(ctx, DrainUpdates()).ConfigureAwait(false);
                continue;
            }

            // A real getMe answers with the bot's identity; the generic body below answers with a
            // message_id, which deserialises to a bot whose username is null.
            if (string.Equals(method, "getMe", StringComparison.Ordinal))
            {
                await RespondAsync(ctx,
                    "{\"ok\":true,\"result\":{\"id\":1,\"username\":"
                    + JsonSerializer.Serialize(BotUsername) + "}}").ConfigureAwait(false);
                continue;
            }

            var call = ctx.Request.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true
                ? ParseMultipart(method, body, ctx.Request.ContentType!)
                : ParseJson(method, body);
            if (call != null) { lock (_gate) _calls.Add(call); }

            await RespondAsync(ctx,
                "{\"ok\":true,\"result\":{\"message_id\":"
                + AssignedMessageId.ToString(CultureInfo.InvariantCulture) + "}}").ConfigureAwait(false);
        }
    }

    /// <summary>Latin-1 keeps one byte to one char, so a PNG survives the round trip well enough to
    /// be measured and the multipart boundaries stay findable.</summary>
    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.Latin1);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task RespondAsync(HttpListenerContext ctx, string body,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static BotCall? ParseJson(string method, string body)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { return null; }

        return new BotCall(
            method,
            Str(root, "text"),
            Str(root, "caption"),
            Bool(root, "disable_notification"),
            Num(root, "reply_to_message_id"),
            Bool(root, "allow_sending_without_reply"),
            Num(root, "message_thread_id"),
            null, null, 0, Str(root, "chat_id"));
    }

    /// <summary>A hand-rolled multipart reader, deliberately: the point of this stub is to see the
    /// bytes the engine produced, and a library that re-normalises them would hide exactly the field
    /// names (<c>photo</c> versus <c>document</c>) the assertions turn on.</summary>
    private static BotCall ParseMultipart(string method, string body, string contentType)
    {
        var boundary = "--" + contentType.Split("boundary=")[^1].Trim('"', ' ');
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? fileField = null, fileName = null;
        long fileBytes = 0;

        foreach (var raw in body.Split(boundary, StringSplitOptions.None))
        {
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0) continue;
            var headers = raw[..split];
            var value = raw[(split + 4)..].TrimEnd('\r', '\n', '-');
            // .NET quotes these; a hand-built body or another runtime need not. Accept both rather
            // than silently skipping every part, which is how this stub first reported "no file".
            var name = Param(headers, "name");
            if (name == null) continue;

            var filename = Param(headers, "filename");
            // value is Latin-1 so the file's byte count is exact; the TEXT fields have to be decoded
            // back out of it, or every "·" in the identity stamp reads as "Â·".
            if (filename != null) { fileField = name; fileName = Utf8(filename); fileBytes = value.Length; }
            else fields[name] = Utf8(value);
        }

        return new BotCall(
            method,
            fields.GetValueOrDefault("text"),
            fields.GetValueOrDefault("caption"),
            string.Equals(fields.GetValueOrDefault("disable_notification"), "True", StringComparison.OrdinalIgnoreCase),
            Parse(fields.GetValueOrDefault("reply_to_message_id")),
            string.Equals(fields.GetValueOrDefault("allow_sending_without_reply"), "True", StringComparison.OrdinalIgnoreCase),
            Parse(fields.GetValueOrDefault("message_thread_id")),
            fileField, fileName, fileBytes, fields.GetValueOrDefault("chat_id"));
    }

    /// <summary>Reads <c>key=value</c> or <c>key="value"</c> out of a Content-Disposition header.
    /// The search is anchored on a leading space so <c>name</c> does not match inside
    /// <c>filename</c>.</summary>
    private static string? Param(string headers, string key)
    {
        var i = headers.IndexOf(" " + key + "=", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = headers[(i + key.Length + 2)..];
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf('"', 1);
            return end < 0 ? null : rest[1..end];
        }
        var stop = rest.IndexOfAny([';', '\r', '\n']);
        return (stop < 0 ? rest : rest[..stop]).Trim();
    }

    private static string Utf8(string latin1) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(latin1));

    private static long? Parse(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { }
        try { _listener.Close(); } catch (Exception) { }
    }
}
