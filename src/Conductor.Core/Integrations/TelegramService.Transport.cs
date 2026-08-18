using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Integrations;

public sealed partial class TelegramService
{
    /// <summary>K5.4 — a thread per run. Telegram gives no way to create a conversation thread in an
    /// ordinary chat, so the run's FIRST delivered message becomes the anchor and every later one
    /// replies to it: the client then renders the run as one collapsible exchange instead of N loose
    /// lines interleaved with whatever else is in the chat. Keyed by chat, because one run can be
    /// pushing to several. A forum supergroup with <c>MessageThreadId</c> configured uses the real
    /// topic instead and never touches this.</summary>
    private readonly ConcurrentDictionary<string, long> _runAnchors = new(StringComparer.Ordinal);

    /// <summary>The one place a Bot API payload gets the fields that are true of EVERY call —
    /// threading and loudness. Anything added here is added to text, photos and documents at once,
    /// which is the mistake the identity stamp already taught this class (FU-OWNER-11).</summary>
    private Dictionary<string, object> BasePayload(string chatId, PushSeverity severity)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal) { ["chat_id"] = chatId };

        if (_cfg?.MessageThreadId is { } topic and > 0)
            p["message_thread_id"] = topic;
        else if (_runAnchors.TryGetValue(chatId, out var anchor))
        {
            p["reply_to_message_id"] = anchor;
            // The owner deleting the anchor must not silence the rest of the run: without this,
            // Telegram answers 400 "message to be replied not found" and drops the push.
            p["allow_sending_without_reply"] = true;
        }

        if (severity == PushSeverity.Quiet) p["disable_notification"] = true;
        return p;
    }

    /// <summary>Posts a JSON payload and returns the <c>message_id</c> Telegram assigned, so the
    /// first message of a run can become its anchor. Throws on a non-success status exactly as
    /// before — the send loop is what decides whether that is fatal.</summary>
    private async Task<long?> PostJsonAsync(string method, Dictionary<string, object> payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_apiBase}{_token}/{method}", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ReadMessageId(body);
    }

    private static long? ReadMessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var r)
                && r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("message_id", out var id)
                && id.TryGetInt64(out var value))
                return value;
        }
        catch (JsonException) { /* a stub or a proxy that answers something else is not a send failure */ }
        return null;
    }

    private void RememberAnchor(string chatId, long? messageId)
    {
        if (messageId is { } id) _runAnchors.TryAdd(chatId, id);
    }

    /// <summary>The wire path for text. Two things happen here that did not before: the message is
    /// SPLIT at 4096 characters rather than being handed to Telegram to reject whole, and each chunk
    /// carries the run's threading and loudness. The identity stamp still goes on at this single
    /// choke point — on the FIRST chunk only, because repeating it on every chunk of a long message
    /// is noise, and the chunks are threaded together anyway.</summary>
    private async Task SendTextAsync(OutboundMessage item, CancellationToken ct)
    {
        var stamped = FormattableString.Invariant($"{_composer.Stamp(item.SessionNumber, item.StageId)}\n{item.Text}");
        var chunks = HtmlChunker.Split(stamped, TelegramLimits.MaxMessageChars);
        // The "(2/4)" counter is appended AFTER the split, so it has to be paid for BEFORE it: a
        // chunk that came back exactly at the limit was pushed 13 characters over it by its own
        // marker, and Telegram refused the three chunks that mattered. Only the multi-chunk path
        // pays the reserve, so an ordinary message is still split at the full limit — i.e. not at all.
        if (chunks.Count > 1) chunks = HtmlChunker.Split(stamped, TelegramLimits.MaxMessageChars - ChunkCounterReserve);

        for (var i = 0; i < chunks.Count; i++)
        {
            var payload = BasePayload(item.ChatId, item.Severity);
            payload["text"] = chunks.Count == 1
                ? chunks[i]
                : FormattableString.Invariant($"{chunks[i]}\n<i>({i + 1}/{chunks.Count})</i>");
            payload["parse_mode"] = "HTML";

            // KS11.1: the seam names buttons; turning them into an inline keyboard is this
            // adapter's job, and it happens here rather than during composition.
            if (item.Buttons is { Count: > 0 } buttons && i == chunks.Count - 1)
            {
                using var kbDoc = JsonDocument.Parse(KeyboardFor(buttons));
                payload["reply_markup"] = kbDoc.RootElement.Clone();
            }

            RememberAnchor(item.ChatId, await PostJsonAsync("sendMessage", payload, ct).ConfigureAwait(false));
        }
    }

    /// <summary>The wire path for a file — K5.3's evidence, actually arriving. <c>sendPhoto</c> when
    /// the artifact is visual and small enough for it, <c>sendDocument</c> otherwise; an artifact
    /// too large for either is announced as text that SAYS it was too large, rather than being
    /// dropped with a 400 in the run log.</summary>
    private async Task SendAttachmentAsync(OutboundMessage item, OutboundAttachment att, CancellationToken ct)
    {
        var bytes = FileSize(att.Path);
        var method = bytes >= 0 ? TelegramLimits.MethodFor(att.AsPhoto, bytes) : null;
        if (method == null)
        {
            var why = bytes < 0 ? "the file is not readable from the engine"
                                : $"{bytes / (1024.0 * 1024.0):0.#} MB is over Telegram's {TelegramLimits.MaxDocumentBytes / (1024 * 1024)} MB limit";
            await SendTextAsync(item with { Text = $"{item.Text}\n<i>not attached — {MessageComposer.EscapeHtml(why)}</i>" }, ct)
                .ConfigureAwait(false);
            return;
        }

        // MultipartFormDataContent owns every part it is given and disposes them with itself; the
        // parts are therefore created inside AddField/AddFile, which hand ownership over on the same
        // line, rather than as locals this method would be responsible for.
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in BasePayload(item.ChatId, item.Severity))
            AddField(form, k, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");

        var caption = FormattableString.Invariant($"{_composer.Stamp(item.SessionNumber, item.StageId)}\n{att.Caption}");
        AddField(form, "caption", MessageComposer.Clip(caption, TelegramLimits.MaxCaptionChars));
        AddField(form, "parse_mode", "HTML");
        await AddFileAsync(form, method == "sendPhoto" ? "photo" : "document", att.Path, ct).ConfigureAwait(false);

        var resp = await _http.PostAsync($"{_apiBase}{_token}/{method}", form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        RememberAnchor(item.ChatId, ReadMessageId(body));
    }

    /// <remarks>The null-out is not ceremony: <see cref="MultipartFormDataContent"/> takes ownership
    /// only once <c>Add</c> has returned, so a throw from <c>Add</c> leaves a part nobody owns.</remarks>
    private static void AddField(MultipartFormDataContent form, string name, string value)
    {
        StringContent? part = null;
        try
        {
            part = new StringContent(value, Encoding.UTF8);
            form.Add(part, name);
            part = null;
        }
        finally { part?.Dispose(); }
    }

    /// <summary>The bytes themselves. Read into memory rather than streamed: the caller has already
    /// refused anything over 50 MB, and a stream would have to outlive this method's scope while the
    /// form owns it — which is how a file handle gets held open on a repo the run is still writing.</summary>
    private static async Task AddFileAsync(MultipartFormDataContent form, string field, string path,
        CancellationToken ct)
    {
        ByteArrayContent? part = null;
        try
        {
            part = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, field, Path.GetFileName(path));
            part = null;
        }
        finally { part?.Dispose(); }
    }

    /// <summary>Room for the longest "\n&lt;i&gt;(nnn/nnn)&lt;/i&gt;" a chunked message can carry.</summary>
    private const int ChunkCounterReserve = 24;

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return -1; }
    }

    /// <summary>The single exit from this process to Telegram. Every queued item, every command
    /// reply, every digest and every test message goes through here.</summary>
    internal Task SendAsync(OutboundMessage item, CancellationToken ct) =>
        item.Attachment is { } att ? SendAttachmentAsync(item, att, ct) : SendTextAsync(item, ct);

    /// <summary>Convenience for the call sites that only ever send plain text to one chat.</summary>
    internal Task SendAsync(string chatId, string text, CancellationToken ct,
        IReadOnlyList<MessageButton>? buttons = null, int? sessionNumber = null,
        PushSeverity severity = PushSeverity.Quiet) =>
        SendAsync(new OutboundMessage(chatId, text, buttons, null, sessionNumber, severity), ct);

    /// <summary>Telegram's <c>inline_keyboard</c>, one row. KS11.1 moved it out of composition: the
    /// seam decides WHAT to offer, the adapter decides what that looks like on a wire.</summary>
    private static string BuildInlineKeyboard(IReadOnlyList<(string Text, string CallbackData)> buttons)
    {
        var elements = new List<Dictionary<string, string>>(buttons.Count);
        foreach (var (text, data) in buttons)
            elements.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = text,
                ["callback_data"] = data,
            });

        var kb = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["inline_keyboard"] = new[] { elements },
        };

        return JsonSerializer.Serialize(kb, JsonOpts);
    }

    /// <summary>The seam's buttons, as this wire's keyboard. Deliberately NOT an overload of
    /// <see cref="BuildInlineKeyboard"/>: a tuple list and a MessageButton list are close enough
    /// that reflection cannot tell the two apart, and B6.1's suite reaches for that name by
    /// string.</summary>
    private static string KeyboardFor(IReadOnlyList<MessageButton> buttons) =>
        BuildInlineKeyboard([.. buttons.Select(b => (b.Text, b.CallbackData))]);
}
