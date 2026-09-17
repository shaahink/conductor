using System.Globalization;
using System.Text;
using System.Text.Json;

using Conductor.Core.Courier;

namespace Conductor.Core.Integrations;

/// <summary>PK3.1 / D5 - protocol 3's sends on the Bot API: a text, a photo, a document or a media
/// group, a reaction, a delete - and, for every send, the message ids that came back.
///
/// <para>A transport of its own rather than more methods on <see cref="TelegramCourierSource"/>,
/// because two processes need exactly this and nothing else of the courier: the courier behind
/// <c>POST /send</c>, and (D3) a run or <c>conductor say</c> sending directly while the courier is
/// down. Neither polls. Sending is outside the one-consumer rule, which constrains
/// <c>getUpdates</c> only, and nothing in this file calls it.</para>
///
/// <para><b>Ceilings are refused by name, before a byte is uploaded.</b> The messenger refuses a
/// 4097-character message with a 400 and a sentence nobody on this side wrote; the sender learns
/// more from "over Telegram's 4096-character message ceiling" and learns it without the round trip.
/// The numbers are <see cref="TelegramLimits"/>' - the one place this repo keeps them.</para></summary>
public sealed class TelegramSender
{
    /// <summary>The most files one media group may carry.</summary>
    public const int MaxGroupFiles = 10;

    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _token;

    /// <param name="http">The client to send on. Not owned: the courier shares its poller's.</param>
    /// <param name="apiBase">The Bot API base up to and including <c>/bot</c>.</param>
    /// <param name="token">The bot token. Never logged, never in a returned sentence.</param>
    public TelegramSender(HttpClient http, string apiBase, string token)
    {
        _http = http;
        _apiBase = apiBase;
        _token = token;
    }

    /// <summary>The parse mode on the wire for what a sender named, or null for plain text.</summary>
    /// <param name="named">HTML (also the default when nothing is named), MarkdownV2, or none.</param>
    /// <param name="refusal">Why the name is not one of those, or null.</param>
    public static string? ParseModeFor(string? named, out string? refusal)
    {
        refusal = null;
        switch (named?.Trim().ToLowerInvariant())
        {
            case null or "" or "html": return "HTML";
            case "markdownv2": return "MarkdownV2";
            case "none" or "plain": return null;
            default:
                refusal = $"parse mode \"{named}\" is not one Telegram takes here; use HTML, MarkdownV2 or none.";
                return null;
        }
    }

    /// <summary>Why <paramref name="send"/> cannot go out as it stands, by the ceiling it breaks, or
    /// null. Pure but for reading file sizes, so <c>say --dry-run</c> can ask it too.</summary>
    public static string? Refusal(CourierSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var photos = send.Photos ?? [];
        var documents = send.Documents ?? [];
        var files = photos.Count + documents.Count;
        var text = send.Text ?? "";

        if (files == 0 && string.IsNullOrWhiteSpace(text))
            return "a send has to carry text or at least one file.";
        if (photos.Count > 0 && documents.Count > 0)
            return "a media group is photos or documents, not both - Telegram refuses the mix; send them as two.";
        if (files > MaxGroupFiles)
            return $"{Count(files)} files is over Telegram's ceiling of {Count(MaxGroupFiles)} files in one media group; send them in groups of ten.";
        if (files > 1 && send.Buttons is { Count: > 0 })
            return "buttons ride a single message; Telegram takes none on a media group.";
        ParseModeFor(send.ParseMode, out var badMode);
        if (badMode is not null) return badMode;

        if (files == 0 && text.Length > TelegramLimits.MaxMessageChars)
            return $"the text is {Count(text.Length)} characters, over Telegram's {Count(TelegramLimits.MaxMessageChars)}-character message ceiling.";
        if (files > 0 && text.Length > TelegramLimits.MaxCaptionChars)
            return $"the caption is {Count(text.Length)} characters, over Telegram's {Count(TelegramLimits.MaxCaptionChars)}-character caption ceiling.";

        foreach (var photo in photos)
            if (FileRefusal(photo, "photo", TelegramLimits.MaxPhotoBytes, "send it as a document") is { } why) return why;
        foreach (var document in documents)
            if (FileRefusal(document, "document", TelegramLimits.MaxDocumentBytes, "split it or put it somewhere it can be pointed at") is { } why) return why;
        return null;
    }

    private static string? FileRefusal(string path, string kind, long ceiling, string instead)
    {
        var bytes = TelegramService.FileSize(path);
        if (bytes < 0 || !File.Exists(path)) return $"the {kind} {path} is not a readable file.";
        return bytes > ceiling
            ? $"the {kind} {Path.GetFileName(path)} is {Megabytes(bytes)} MB, over Telegram's {Megabytes(ceiling)} MB {kind} ceiling; {instead}."
            : null;
    }

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Sends <paramref name="send"/> to <paramref name="chatId"/> and answers with the ids it
    /// became, or refuses by name. Never throws for the messenger's reasons: the listener prints
    /// the answer, and an exception there is a 500 with no sentence in it.</summary>
    public async Task<CourierAck> SendAsync(CourierSend send, string chatId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (Refusal(send) is { } refused) return new CourierAck(false, refused, null, chatId);

        var mode = ParseModeFor(send.ParseMode, out _);
        var files = send.Files;
        try
        {
            if (files.Count == 0)
            {
                var payload = Base(chatId, send);
                payload["text"] = send.Text!;
                payload["disable_web_page_preview"] = true;
                if (mode is not null) payload["parse_mode"] = mode;
                if (Keyboard(send.Buttons) is { } keyboard) payload["reply_markup"] = keyboard;
                return await PostAsync("sendMessage", chatId, JsonBody(payload), ct).ConfigureAwait(false);
            }

            var asPhoto = send.Photos is { Count: > 0 };
            using var form = new MultipartFormDataContent();
            foreach (var (key, value) in Base(chatId, send))
                TelegramService.AddField(form, key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

            if (files.Count == 1)
            {
                if (!string.IsNullOrEmpty(send.Text)) TelegramService.AddField(form, "caption", send.Text);
                if (mode is not null && !string.IsNullOrEmpty(send.Text)) TelegramService.AddField(form, "parse_mode", mode);
                if (Keyboard(send.Buttons) is { } keyboard)
                    TelegramService.AddField(form, "reply_markup", JsonSerializer.Serialize(keyboard, TelegramService.JsonOpts));
                await TelegramService.AddFileAsync(form, asPhoto ? "photo" : "document", files[0], ct).ConfigureAwait(false);
                return await PostAsync(asPhoto ? "sendPhoto" : "sendDocument", chatId, form, ct).ConfigureAwait(false);
            }

            // A media group: each file is a part named by position and the album refers to it as
            // attach://; the caption rides the first item, which is where every client shows it.
            var media = new List<Dictionary<string, object>>(files.Count);
            for (var i = 0; i < files.Count; i++)
            {
                var item = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = asPhoto ? "photo" : "document",
                    ["media"] = "attach://file" + Count(i),
                };
                if (i == 0 && !string.IsNullOrEmpty(send.Text))
                {
                    item["caption"] = send.Text;
                    if (mode is not null) item["parse_mode"] = mode;
                }
                media.Add(item);
            }
            TelegramService.AddField(form, "media", JsonSerializer.Serialize(media, TelegramService.JsonOpts));
            for (var i = 0; i < files.Count; i++)
                await TelegramService.AddFileAsync(form, "file" + Count(i), files[i], ct).ConfigureAwait(false);
            return await PostAsync("sendMediaGroup", chatId, form, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new CourierAck(false, $"a file on the send could not be read ({ex.Message}).", null, chatId);
        }
    }

    /// <summary>Puts one emoji reaction on a message. Null when it landed, else the reason.</summary>
    public async Task<string?> ReactAsync(string chatId, long messageId, string emoji, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return "a reaction has to name its emoji.";
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["reaction"] = new[]
            {
                new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "emoji", ["emoji"] = emoji.Trim() },
            },
        };
        var ack = await PostAsync("setMessageReaction", chatId, JsonBody(payload), ct).ConfigureAwait(false);
        return ack.Accepted ? null : ack.Detail;
    }

    /// <summary>Deletes one message. Null when it is gone, else the reason - in the messenger's own
    /// words, since "message can't be deleted for everyone" is a rule of theirs this side cannot predict.</summary>
    public async Task<string?> DeleteAsync(string chatId, long messageId, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
        };
        var ack = await PostAsync("deleteMessage", chatId, JsonBody(payload), ct).ConfigureAwait(false);
        return ack.Accepted ? null : ack.Detail;
    }

    /// <summary>The fields every send carries: the chat, the thread it answers, and its loudness.</summary>
    private static Dictionary<string, object> Base(string chatId, CourierSend send)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal) { ["chat_id"] = chatId };
        if (send.ReplyTo is { } replyTo) p["reply_to_message_id"] = replyTo;
        if (send.Silent) p["disable_notification"] = true;
        return p;
    }

    /// <summary>KS11.1's rule: buttons cross the seam as themselves, and only this adapter knows
    /// what an inline keyboard looks like on the wire.</summary>
    internal static object? Keyboard(IReadOnlyList<CourierButton>? buttons) =>
        buttons is { Count: > 0 }
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["inline_keyboard"] = buttons
                    .Select(b => new[]
                    {
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["text"] = b.Text,
                            ["callback_data"] = b.CallbackData,
                        },
                    })
                    .ToArray(),
            }
            : null;

    private static StringContent JsonBody(Dictionary<string, object> payload) =>
        new(JsonSerializer.Serialize(payload, TelegramService.JsonOpts), Encoding.UTF8, "application/json");

    /// <summary>One Bot API call, answered as an ack carrying the ids that came back. The URL carries
    /// the token, so no sentence built here ever quotes it or the exception text of a request to it
    /// beyond the transport's own message.</summary>
    private async Task<CourierAck> PostAsync(string method, string chatId, HttpContent content, CancellationToken ct)
    {
        try
        {
            using (content)
            using (var resp = await _http.PostAsync($"{_apiBase}{_token}/{method}", content, ct).ConfigureAwait(false))
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return new CourierAck(false,
                        $"the bot API refused {method} to chat {chatId} ({((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)}{Description(body)}).",
                        null, chatId);
                return new CourierAck(true, "", IdsIn(body), chatId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new CourierAck(false, $"the bot API could not be reached ({ex.Message}).", null, chatId);
        }
    }

    /// <summary>The ids in a Bot API answer: one for a message, one per item for a media group, none
    /// for a call that answers <c>true</c>.</summary>
    internal static IReadOnlyList<long>? IdsIn(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            var ids = new List<long>();
            if (result.ValueKind == JsonValueKind.Object) Add(result);
            else if (result.ValueKind == JsonValueKind.Array)
                foreach (var item in result.EnumerateArray()) Add(item);
            return ids.Count > 0 ? ids : null;

            void Add(JsonElement message)
            {
                if (message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("message_id", out var id) && id.TryGetInt64(out var value))
                    ids.Add(value);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Description(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? ": " + d.GetString()
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
