using System.Globalization;

using Conductor.Core.Integrations.Messaging;

using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>
/// DV3.1 — the inbound half of the Bot API that this engine could not see.
///
/// <para><c>TgMessage</c> carried <c>message_id</c>, <c>text</c> and <c>chat</c>, and nothing else.
/// A voice note was therefore not refused, not logged and not answered: it was INVISIBLE (findings
/// §1.2 gap 2). What arrives is classified off the PROPERTY it came under, the bytes are fetched —
/// or refused by name — and the result is handed to the seam. What the note MEANS is
/// <see cref="RemoteSurface"/>'s; where it eventually LIVES is DV3.2's.</para>
///
/// <para>DV4.1 moved the fetch itself into <see cref="TelegramMediaFetcher"/>, unchanged, because
/// the courier is a second poller that needs the same 20 MB rule and the same filename scrubbing.
/// What stays here is the part that is about a <c>TgMessage</c>: which of the four kinds this is,
/// which file object carries it, and the <see cref="InboundNote"/> the seam is handed.</para>
///
/// <para>Its own partial for the reason <c>TelegramService.Polling.cs</c> was split out: the main
/// file is a few lines under the 500-line architecture ceiling and every addition to it has to be
/// a split.</para>
/// </summary>
public sealed partial class TelegramService
{
    /// <summary>Where fetched media lands. Under the state dir, so it is per-project, and under a
    /// directory <c>.conductor/.gitignore</c> already denies by default — findings §6.1: this repo
    /// is PUBLIC and the owner's voice notes must never be committed. No allowlist entry is added
    /// for it, now or later.</summary>
    internal string MediaDir => Path.Combine(_plan.StateDir, "inbox", "media");

    /// <summary>Built once, but reading <see cref="MediaDir"/> per call: a live plan reload can move
    /// the state dir under a running service, and a fetcher holding the old path would keep writing
    /// into the project this run no longer is.</summary>
    private TelegramMediaFetcher Media =>
        _media ??= new TelegramMediaFetcher(_http, _apiBase, _token ?? "", () => MediaDir, _log);

    private TelegramMediaFetcher? _media;

    /// <summary>Which kind of file this message carries, or null for plain text. Read off the
    /// PROPERTY, because that is the only place the wire says which of the four it is: an .oga
    /// under <c>voice</c> is a voice note and the same .oga under <c>document</c> is a file
    /// someone attached, and the difference matters downstream.</summary>
    internal static InboundMediaKind? KindOf(TgMessage msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        if (msg.Voice != null) return InboundMediaKind.Voice;
        if (msg.Audio != null) return InboundMediaKind.Audio;
        if (msg.Document != null) return InboundMediaKind.Document;
        if (msg.Photo is { Count: > 0 }) return InboundMediaKind.Photo;
        return null;
    }

    /// <summary>The file object for a kind. For a photo that is the LARGEST of the sizes Telegram
    /// sent — the same picture at five resolutions, and the thumbnail is not what the sender
    /// meant.</summary>
    internal static TgFileRef? FileOf(TgMessage msg, InboundMediaKind kind)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return kind switch
        {
            InboundMediaKind.Voice => msg.Voice,
            InboundMediaKind.Audio => msg.Audio,
            InboundMediaKind.Document => msg.Document,
            _ => msg.Photo?.MaxBy(p => p.FileSize ?? 0),
        };
    }

    /// <summary>The note a media message becomes, once the bytes are on disk or refused. Shared with
    /// the courier (DV4.1) so one wire shape produces one seam record: a second construction of this
    /// would be a second chance for a routing field to go missing, and DV3.4 routes on two of
    /// them.</summary>
    internal static InboundNote NoteFrom(TgMessage msg, InboundMedia? media, string chatId, long updateId)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return new InboundNote(
            chatId,
            msg.MessageId,
            (msg.Caption ?? msg.Text ?? "").Trim(),
            media,
            msg.ReplyToMessage?.MessageId,
            msg.ReplyToMessage?.Text ?? msg.ReplyToMessage?.Caption,
            msg.MessageThreadId,
            updateId);
    }

    /// <summary>A message that carries a file: fetched if this chat may file, refused by name if it
    /// is too big, and acknowledged either way.</summary>
    private async Task HandleMediaMessageAsync(string chatId, ChatProfile profile, TgMessage msg,
        InboundMediaKind kind, long updateId, CancellationToken ct)
    {
        var file = FileOf(msg, kind);
        InboundMedia? media = null;

        // The profile gate runs BEFORE the fetch, not after: an observer must not be able to put
        // bytes on this machine by sending them, so nothing is downloaded on their behalf at all.
        if (file != null && ChatProfiles.MayFile(profile))
            media = await Media.FetchAsync(kind, file, msg.MessageId, ct).ConfigureAwait(false);
        else if (file != null)
            media = TelegramMediaFetcher.Undownloaded(kind, file, null);

        var note = NoteFrom(msg, media, chatId, updateId);

        // One line per inbound note, with the two facts that decide WHERE it belongs (DV3.4): the
        // push it answers and the forum topic it arrived in. Neither is stored yet, and a routing
        // hint that is never written down anywhere is a routing hint nobody can debug.
        _log.LogInformation(
            "Telegram inbound note: {Kind} from chat {Chat}, message {MessageId}, reply to {ReplyTo}, topic {Topic}, text {Chars} chars",
            KindLabel(media), chatId, msg.MessageId,
            note.ReplyToMessageId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            note.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            note.Text.Length);

        await _surface.HandleNoteAsync(note, profile, ct).ConfigureAwait(false);
    }

    internal static string KindLabel(InboundMedia? media) =>
        media is null ? "text" : media.Kind.ToString().ToLowerInvariant();
}
