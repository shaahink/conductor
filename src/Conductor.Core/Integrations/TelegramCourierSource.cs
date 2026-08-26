using System.Globalization;

using Conductor.Core.Courier;
using Conductor.Core.Integrations.Messaging;

using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>DV4.1 — the courier's wire. The only file in the courier that knows what Telegram is.
///
/// <para>It is the adapter half of KS11.1's rule, applied to a second consumer of the same Bot API:
/// unwrap the envelope, decide whether this chat is one of ours, fetch or refuse the bytes, and hand
/// the seam an <see cref="InboundNote"/> with no <c>Tg</c> type anywhere on it. Everything about
/// ORDER — the durable offset, the replay, the dedup — is <c>CourierDaemon</c>'s, and none of it is
/// visible from here.</para>
///
/// <para>Two things it does NOT do, both deliberate. It does not queue or fan out: that is
/// <c>IMessageChannel</c>'s job for a run with pushes to flush, and a courier has no run. And it
/// does not resolve the token from a plan — the courier owns <c>CONDUCTOR_TELEGRAM_TOKEN</c> at
/// machine level (findings §1.4-B), because a machine-level daemon reading one project's secrets
/// file is one project deciding who may write to all the others.</para>
///
/// <para><b>The 24-hour limit (§6.3).</b> Telegram holds an undelivered update for 24 hours. That is
/// the outer bound on everything below: the courier answers "no run live", not "machine off".</para>
/// </summary>
public sealed class TelegramCourierSource : ICourierSource, IDisposable
{
    /// <summary>The env var the courier owns. Same name the run reads, which is the point: one token,
    /// one consumer, and §6.9's precedence rule is what settles which process it belongs to.</summary>
    public const string TokenEnvVar = "CONDUCTOR_TELEGRAM_TOKEN";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(65) };
    private readonly CourierSettings _settings;
    private readonly TelegramMediaFetcher _media;
    private readonly string _apiBase;
    private readonly string _token;
    private readonly string _mediaDir;
    private readonly ILogger _log;

    /// <param name="token">The bot token. Resolved by the caller so the refusal for a missing one is
    /// a CLI sentence naming where to put it, not an exception from inside a poll loop.</param>
    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    public TelegramCourierSource(CourierSettings settings, string token,
        ILogger log, string? stateHomeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _token = token;
        _log = log;

        var root = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            ? TelegramService.DefaultApiRoot : settings.ApiBaseUrl!.Trim();
        _apiBase = root.TrimEnd('/') + "/bot";
        _mediaDir = CourierHome.MediaDirFor(stateHomeRoot);
        _media = new TelegramMediaFetcher(_http, _apiBase, _token, () => _mediaDir, log);
    }

    /// <summary>The bot, by name where it will say — never the token, which shares a string with the
    /// URL and has ended up in a log file in more than one project's history.</summary>
    public string Describe => "the telegram bot";


    /// <summary>Where bytes land before they are adopted into a project's inbox. Public so
    /// <c>courier status</c> can name a directory a person may want to look in.</summary>
    public string MediaDir => _mediaDir;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CourierDelivery>> FetchAsync(long offset, CancellationToken ct)
    {
        IReadOnlyList<TgUpdate> updates;
        try
        {
            updates = await TelegramService.GetUpdatesAsync(_http, _apiBase, _token, offset,
                TelegramService.LongPollSeconds, ct).ConfigureAwait(false);
        }
        catch (TelegramConflictException ex)
        {
            // Translated at the boundary: the daemon backs off and says so without ever learning
            // which messenger imposed the one-consumer rule (findings §6.9).
            throw new CourierConflictException(ex.Message, ex);
        }

        var deliveries = new List<CourierDelivery>(updates.Count);
        foreach (var update in updates)
        {
            deliveries.Add(await DeliveryOfAsync(update, ct).ConfigureAwait(false));
        }
        return deliveries;
    }

    /// <summary>One update as the seam sees it — or <see cref="CourierDelivery.Ignored"/> when there
    /// is nothing here for a courier.
    ///
    /// <para>Ignored is not dropped. Every update comes back as a delivery so the daemon advances its
    /// offset past it; hand back nothing and the same message is fetched again on every poll for the
    /// next 24 hours. What ignored means is that nobody is ANSWERED — an unlisted chat gets silence,
    /// because a bot that argues with a stranger has told them it exists.</para></summary>
    private async Task<CourierDelivery> DeliveryOfAsync(TgUpdate update, CancellationToken ct)
    {
        if (update.Message is not { } msg) return CourierDelivery.Ignored(update.UpdateId);

        var chatId = msg.Chat?.Id.ToString(CultureInfo.InvariantCulture);
        if (_settings.ProfileFor(chatId) is not { } profile)
        {
            _log.LogDebug("Courier ignored a message from unlisted chat {Chat}", chatId ?? "none");
            return CourierDelivery.Ignored(update.UpdateId);
        }

        var kind = TelegramService.KindOf(msg);
        InboundMedia? media = null;
        if (kind is { } k && TelegramService.FileOf(msg, k) is { } file)
        {
            // The profile gate runs BEFORE the fetch. An observer must not be able to put bytes on
            // this machine by sending them, so nothing is downloaded on their behalf at all.
            media = ChatProfiles.MayFile(profile)
                ? await _media.FetchAsync(k, file, msg.MessageId, ct).ConfigureAwait(false)
                : TelegramMediaFetcher.Undownloaded(k, file, null);
        }

        var note = TelegramService.NoteFrom(msg, media, chatId!, update.UpdateId);
        var command = CommandIn(msg.Text, media);

        _log.LogInformation(
            "Courier inbound {Kind} from chat {Chat}, update {UpdateId}, reply to {ReplyTo}, topic {Topic}, text {Chars} chars",
            TelegramService.KindLabel(media), chatId, update.UpdateId,
            note.ReplyToMessageId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            note.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            note.Text.Length);

        return new CourierDelivery(update.UpdateId, note, profile, command);
    }

    /// <summary>The slash command in a message, without its slash and without any <c>@botname</c>
    /// suffix a group client appends, or null when this is a note rather than an instruction.
    ///
    /// <para>A message that carries a file is never a command, whatever its caption says: the caption
    /// is what the sender said ABOUT the file, and reading it as an instruction is how a voice note
    /// captioned "/project is wrong" reconfigures the machine instead of being filed.</para></summary>
    internal static string? CommandIn(string? text, InboundMedia? media)
    {
        if (media is not null) return null;
        var trimmed = text?.Trim();
        if (trimmed is not { Length: > 1 } || trimmed[0] != '/') return null;

        var body = trimmed[1..];
        var at = body.IndexOf('@', StringComparison.Ordinal);
        var space = body.IndexOf(' ', StringComparison.Ordinal);
        if (at < 0 || (space >= 0 && at > space)) return body;
        return body[..at] + (space >= 0 ? body[space..] : "");
    }

    /// <inheritdoc />
    public async Task ReplyAsync(string chatId, string text, long? threadId, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
        };
        if (threadId is { } thread) payload["message_thread_id"] = thread;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload, TelegramService.JsonOpts);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_apiBase}{_token}/sendMessage", content, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Courier reply to chat {Chat} was refused: {Status}", chatId, resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      && !ct.IsCancellationRequested)
        {
            // A reply that does not arrive costs the receipt, never the note: the note is already on
            // disk by the time this runs, which is the ordering RemoteSurface established at DV3.1.
            _log.LogWarning(ex, "Courier reply to chat {Chat} failed", chatId);
        }
    }

    public void Dispose() => _http.Dispose();
}
