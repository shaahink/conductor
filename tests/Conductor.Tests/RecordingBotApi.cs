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
    string? ChatId = null,
    string? ReplyMarkup = null)
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
        lock (_gate) _pending.Enqueue(id =>
            "\"message\":{\"message_id\":" + id.ToString(CultureInfo.InvariantCulture)
            + ",\"chat\":{\"id\":" + chatId + "}"
            + ",\"text\":" + JsonSerializer.Serialize(text) + "}");
    }

    /// <summary>DV3.1 - queue a WHOLE message object, verbatim. A voice note, a document, a photo
    /// array, a reply, a forum topic: every one of them is a shape <see cref="QueueCommand"/> cannot
    /// express, and the point of driving them through this stub is that the engine deserialises the
    /// same JSON Telegram sends rather than a DTO a test handed it.</summary>
    /// <param name="messageJson">The complete <c>message</c> object, including <c>message_id</c> and
    /// <c>chat</c>. The <c>update_id</c> around it is this stub's to assign.</param>
    public void QueueMessage(string messageJson)
    {
        lock (_gate) _pending.Enqueue(_ => "\"message\":" + messageJson);
    }

    /// <summary>DV4.4 - queue a BUTTON PRESS. Not a message: a <c>callback_query</c> update has no
    /// <c>message</c> field at all, which is precisely the shape that made the courier discard every
    /// press it was ever sent, and a stub that can only wrap messages cannot show that.</summary>
    /// <param name="chatId">The chat the keyboard was posted in - what the profile is resolved from.</param>
    /// <param name="data">The <c>callback_data</c> the button carried.</param>
    /// <param name="fromId">The presser, or null for the chat itself. In a group these differ, and
    /// the answer goes to the presser.</param>
    public void QueueCallback(string chatId, string data, string? fromId = null)
    {
        lock (_gate) _pending.Enqueue(id =>
            "\"callback_query\":{\"id\":\"cb" + id.ToString(CultureInfo.InvariantCulture) + "\""
            + ",\"from\":{\"id\":" + (fromId ?? chatId) + "}"
            + ",\"message\":{\"message_id\":" + id.ToString(CultureInfo.InvariantCulture)
            + ",\"chat\":{\"id\":" + chatId + "}}"
            + ",\"data\":" + JsonSerializer.Serialize(data) + "}");
    }

    /// <summary>Each entry builds one message body once the stub has assigned its update id - the
    /// id a text command uses as its <c>message_id</c> too, which is how it has always read.</summary>
    private readonly Queue<Func<int, string>> _pending = new();
    private int _nextUpdateId = 1;

    private readonly Dictionary<string, (string Path, byte[] Bytes)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fileErrors = new(StringComparer.Ordinal);
    private int _getFileCalls;

    /// <summary>DV3.1 - register bytes this stub will serve for a <c>file_id</c>: <c>getFile</c>
    /// answers with <paramref name="filePath"/> and a GET of <c>/file/bot&lt;token&gt;/{path}</c>
    /// hands back exactly these bytes. Returns the path, so a test can assert what the engine was
    /// told to fetch.</summary>
    public string AddFile(string fileId, string filePath, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate) _files[fileId] = (filePath, bytes);
        return filePath;
    }

    /// <summary>DV3.1 - make <c>getFile</c> REFUSE this file_id with the Bot API's own words. The
    /// 20 MB ceiling is enforced on the server: an oversize file has a perfectly good file_id and
    /// no downloadable path, which is a different failure from a missing file.</summary>
    public void RefuseFile(string fileId, string description)
    {
        lock (_gate) _fileErrors[fileId] = description;
    }

    /// <summary>How many <c>getFile</c> calls arrived. Proving a fetch did NOT happen - for a chat
    /// that may not file, or for a size refused before the round trip - needs a counter, because
    /// "no call recorded" is also what a broken test looks like.</summary>
    public int GetFileCalls { get { lock (_gate) return _getFileCalls; } }

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

    /// <summary>DV4.1 — behave like the real Bot API's confirmation protocol instead of handing each
    /// update over once.
    ///
    /// <para>Off by default, so no existing test moves: those drive a run's poll loop, which keeps its
    /// offset in a field, and re-serving an update to it would be a livelock rather than a test. The
    /// courier's whole claim is about what happens when a process DIES holding an unconfirmed update,
    /// and that cannot be asserted against a stub that has already forgotten the update. With this on,
    /// an update is re-served until a <c>getUpdates?offset=</c> above its id confirms it — which is
    /// what api.telegram.org does, and the reason a restart replays at all.</para></summary>
    public bool HonourOffset { get; set; }

    /// <summary>The highest <c>offset</c> this stub has been asked with. A test that wants to prove
    /// the courier acknowledged something needs to see the acknowledgement, not infer it.</summary>
    public long LastOffsetSeen { get { lock (_gate) return _lastOffset; } }

    private long _lastOffset;
    private readonly List<(int Id, string Json)> _unconfirmed = new();

    /// <summary>Hands over every queued command. With <see cref="HonourOffset"/> off, once — a
    /// long-poll that kept re-serving the same update would have the engine answer it on every tick.
    /// With it on, everything not yet confirmed by an offset, exactly as the real API does.</summary>
    private string DrainUpdates(string query)
    {
        List<(int Id, string Json)> serve;
        lock (_gate)
        {
            var offset = OffsetIn(query);
            if (offset > _lastOffset) _lastOffset = offset;
            if (HonourOffset && offset > 0) _unconfirmed.RemoveAll(u => u.Id < offset);

            serve = HonourOffset ? new List<(int, string)>(_unconfirmed) : new List<(int, string)>();
            while (_pending.Count > 0)
            {
                var build = _pending.Dequeue();
                var id = _nextUpdateId++;
                var item = (id, build(id));
                serve.Add(item);
                if (HonourOffset) _unconfirmed.Add(item);
            }
            if (serve.Count == 0) return """{"ok":true,"result":[]}""";
        }

        var sb = new StringBuilder("""{"ok":true,"result":[""");
        for (var i = 0; i < serve.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"update_id\":").Append(serve[i].Id.ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(serve[i].Json)
              .Append('}');
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>The <c>offset</c> query parameter, or 0. Parsed rather than assumed: the whole point
    /// is that the stub answers what it was ASKED, so a courier that never advances its offset gets
    /// the same update back.</summary>
    private static long OffsetIn(string query)
    {
        var key = "offset=";
        var at = query.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return 0;
        var rest = query[(at + key.Length)..];
        var end = rest.IndexOf('&', StringComparison.Ordinal);
        var value = end < 0 ? rest : rest[..end];
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
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

            var path = ctx.Request.Url?.AbsolutePath ?? "";

            // DV3.1: the download endpoint is NOT /bot<token>/<method> - it is
            // /file/bot<token>/<file_path>, so it has to be recognised by prefix before the last
            // path segment is read as a method name.
            if (path.Contains("/file/bot", StringComparison.Ordinal))
            {
                await ServeFileAsync(ctx, path).ConfigureAwait(false);
                continue;
            }

            var method = path.Split('/')[^1];
            var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);

            if (string.Equals(method, "getFile", StringComparison.Ordinal))
            {
                await RespondAsync(ctx, GetFileBody(ctx.Request.Url?.Query ?? "")).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(method, "getUpdates", StringComparison.Ordinal))
            {
                lock (_gate) _polls++;
                var conflict = ConflictBody;
                if (conflict != null)
                {
                    await RespondAsync(ctx, conflict, HttpStatusCode.Conflict).ConfigureAwait(false);
                    continue;
                }
                await RespondAsync(ctx, DrainUpdates(ctx.Request.Url?.Query ?? "")).ConfigureAwait(false);
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

            // DV4.4: a GET with no body is still a call the engine made. answerCallbackQuery is one,
            // and it was invisible here — which matters because "no call recorded" is also what a
            // missing call looks like, and the Bot API's obligation to answer a press is exactly the
            // kind of thing that gets dropped in a refactor and noticed by nobody.
            var call = body.Length == 0
                ? new BotCall(method, ctx.Request.Url?.Query, null, false, null, false, null, null, null, 0)
                : ctx.Request.ContentType?.Contains("multipart/", StringComparison.OrdinalIgnoreCase) == true
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
            null, null, 0, Str(root, "chat_id"),
            root.TryGetProperty("reply_markup", out var markup) ? markup.GetRawText() : null);
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

    /// <summary>The <c>getFile</c> answer for a file_id: a path, a refusal in the API's own words,
    /// or the invalid-file_id error a made-up handle really gets.</summary>
    private string GetFileBody(string query)
    {
        var fileId = "";
        foreach (var part in query.TrimStart('?').Split('&'))
            if (part.StartsWith("file_id=", StringComparison.Ordinal))
                fileId = Uri.UnescapeDataString(part["file_id=".Length..]);

        lock (_gate)
        {
            _getFileCalls++;
            if (_fileErrors.TryGetValue(fileId, out var why))
                return "{\"ok\":false,\"error_code\":400,\"description\":" + JsonSerializer.Serialize(why) + "}";
            if (_files.TryGetValue(fileId, out var f))
                return "{\"ok\":true,\"result\":{\"file_id\":" + JsonSerializer.Serialize(fileId)
                     + ",\"file_size\":" + f.Bytes.Length.ToString(CultureInfo.InvariantCulture)
                     + ",\"file_path\":" + JsonSerializer.Serialize(f.Path) + "}}";
        }
        return """{"ok":false,"error_code":400,"description":"Bad Request: invalid file_id"}""";
    }

    /// <summary>Serves the registered bytes for <c>/file/bot&lt;token&gt;/&lt;file_path&gt;</c>. The
    /// token segment is skipped rather than matched: it is a secret on a developer machine and this
    /// stub has never recorded one.</summary>
    private async Task ServeFileAsync(HttpListenerContext ctx, string path)
    {
        var marker = path.IndexOf("/file/bot", StringComparison.Ordinal) + "/file/bot".Length;
        var afterToken = path.IndexOf('/', marker);
        var wanted = afterToken < 0 ? "" : path[(afterToken + 1)..];

        byte[]? bytes = null;
        lock (_gate)
        {
            foreach (var f in _files.Values)
                if (string.Equals(f.Path, wanted, StringComparison.Ordinal)) { bytes = f.Bytes; break; }
        }

        if (bytes == null)
        {
            await RespondAsync(ctx, """{"ok":false,"error_code":404,"description":"Not Found"}""",
                HttpStatusCode.NotFound).ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { }
        try { _listener.Close(); } catch (Exception) { }
    }
}
