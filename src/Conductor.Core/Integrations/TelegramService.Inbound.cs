using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Conductor.Core.Integrations.Messaging;

using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>
/// DV3.1 — the inbound half of the Bot API that this engine could not see.
///
/// <para><c>TgMessage</c> carried <c>message_id</c>, <c>text</c> and <c>chat</c>, and nothing else.
/// A voice note was therefore not refused, not logged and not answered: it was INVISIBLE (findings
/// §1.2 gap 2). Everything here is the wire protocol for making it visible — classify what came
/// under which property, ask <c>getFile</c> where the bytes are, fetch them, and put a file on
/// disk. What the note MEANS is <see cref="RemoteSurface"/>'s; where it eventually LIVES is
/// DV3.2's.</para>
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
            media = await FetchAsync(kind, file, msg.MessageId, ct).ConfigureAwait(false);
        else if (file != null)
            media = Undownloaded(kind, file, null);

        var note = new InboundNote(
            chatId,
            msg.MessageId,
            (msg.Caption ?? msg.Text ?? "").Trim(),
            media,
            msg.ReplyToMessage?.MessageId,
            msg.ReplyToMessage?.Text ?? msg.ReplyToMessage?.Caption,
            msg.MessageThreadId,
            updateId);

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

    private static string KindLabel(InboundMedia? media) =>
        media is null ? "text" : media.Kind.ToString().ToLowerInvariant();

    /// <summary>getFile, then the bytes. Every exit hands back an <see cref="InboundMedia"/> — one
    /// with a path when the download worked, one with a REASON when it did not. There is no exit
    /// that returns null and lets the message evaporate, which is the defect this checkpoint
    /// exists to close.</summary>
    private async Task<InboundMedia> FetchAsync(InboundMediaKind kind, TgFileRef file, long messageId,
        CancellationToken ct)
    {
        var name = DisplayName(kind, file);
        var declared = file.FileSize ?? 0;

        // Checked here first because the message itself usually declares the size, and refusing on
        // what we already know saves a round trip to an API that is going to say no.
        if (declared > TelegramLimits.MaxDownloadBytes)
            return Undownloaded(kind, file, TooBig(name, declared));

        TgFileResponse? found;
        try
        {
            var url = $"{_apiBase}{_token}/getFile?file_id={Uri.EscapeDataString(file.FileId)}";
            found = await _http.GetFromJsonAsync<TgFileResponse>(url, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or JsonException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Telegram getFile failed for {Name}", name);
            return Undownloaded(kind, file, NotFetched(name, ex.Message));
        }

        if (found is not { Ok: true, Result.FilePath: { Length: > 0 } path })
            return Undownloaded(kind, file, RefuseFromApi(name, declared, found));

        var size = found.Result.FileSize ?? declared;
        if (size > TelegramLimits.MaxDownloadBytes)
            return Undownloaded(kind, file, TooBig(name, size));

        return await DownloadAsync(kind, file, name, path, messageId, ct).ConfigureAwait(false);
    }

    /// <summary>Telegram's own words about a getFile it would not serve, plus the cap when the words
    /// say what the cap says. The API's "file is too big" is the second half of the 20 MB rule — the
    /// half that fires when the message never declared a size.</summary>
    private static string RefuseFromApi(string name, long declared, TgFileResponse? resp)
    {
        var why = resp?.Description is { Length: > 0 } d ? d : "the Bot API returned no file path";
        return why.Contains("too big", StringComparison.OrdinalIgnoreCase)
            ? (declared > 0
                ? TooBig(name, declared)
                : NotFetched(name, why + $"; a bot may download at most {TelegramLimits.MaxDownloadLabel}"))
            : NotFetched(name, why);
    }

    /// <summary>Streams the bytes to disk with the cap enforced a THIRD time, against what actually
    /// arrives. The first two checks trust numbers the other end supplied; this one does not, so a
    /// lying <c>file_size</c> cannot fill the disk.</summary>
    private async Task<InboundMedia> DownloadAsync(InboundMediaKind kind, TgFileRef file, string name,
        string filePath, long messageId, CancellationToken ct)
    {
        var target = Path.Combine(MediaDir, StoredName(kind, file, filePath, messageId));
        try
        {
            Directory.CreateDirectory(MediaDir);
            var url = FileBase + filePath;
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var written = await CopyCappedAsync(resp, target, ct).ConfigureAwait(false);
            if (written < 0)
            {
                TryDelete(target);
                return Undownloaded(kind, file, TooBig(name, TelegramLimits.MaxDownloadBytes + 1));
            }

            _log.LogInformation("Telegram inbound {Kind} saved: {Path} ({Bytes} B)", kind, target, written);
            return new InboundMedia(kind, file.FileId, name, file.MimeType, written, file.Duration, target, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            TryDelete(target);
            _log.LogWarning(ex, "Telegram file download failed for {Name}", name);
            return Undownloaded(kind, file, NotFetched(name, ex.Message));
        }
    }

    /// <summary>Bytes written, or -1 if the stream went over the cap — in which case the copy stops
    /// there rather than reading the rest of it.</summary>
    private static async Task<long> CopyCappedAsync(HttpResponseMessage resp, string target, CancellationToken ct)
    {
        // The repo's `await using (x.ConfigureAwait(false))` idiom (EventLog.cs:86): the declaration
        // form cannot carry ConfigureAwait without turning the variable into something that is no
        // longer a Stream, so the scope and the variable are written separately.
        var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var sink = File.Create(target);
            await using (sink.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > TelegramLimits.MaxDownloadBytes) return -1;
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                return written;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static InboundMedia Undownloaded(InboundMediaKind kind, TgFileRef file, string? refusal) =>
        new(kind, file.FileId, DisplayName(kind, file), file.MimeType, file.FileSize ?? 0,
            file.Duration, null, refusal);

    /// <summary>What to CALL it in a sentence to a human. A document has the sender's own name; the
    /// other three kinds have none on the wire, so they get the noun, which is what the sender would
    /// have called it anyway.</summary>
    internal static string DisplayName(InboundMediaKind kind, TgFileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FileName is { Length: > 0 } given) return Leaf(given);
        return kind switch
        {
            InboundMediaKind.Voice => "voice note",
            InboundMediaKind.Audio => "audio",
            InboundMediaKind.Document => "document",
            _ => "photo",
        };
    }

    /// <summary>What to call it ON DISK. Prefixed with the message id so two notes never collide,
    /// and built from a SCRUBBED leaf: a sender controls <c>file_name</c>, so
    /// <c>../../../.conductor/plan.json</c> is a name this method must make harmless, not a path it
    /// may join.</summary>
    internal static string StoredName(InboundMediaKind kind, TgFileRef file, string filePath, long messageId)
    {
        ArgumentNullException.ThrowIfNull(file);
        var prefix = messageId.ToString(CultureInfo.InvariantCulture) + "-";
        if (file.FileName is { Length: > 0 } given)
        {
            var safe = Scrub(Leaf(given));
            if (safe.Length > 0) return prefix + safe;
        }

        // The dot is put back deliberately: Scrub strips leading and trailing dots (a name that is
        // only dots is a directory reference), which silently turned ".oga" into "oga" and made
        // every stored voice note extensionless.
        var ext = Scrub(Path.GetExtension(Leaf(filePath ?? "")).TrimStart('.'));
        return prefix + kind.ToString().ToLowerInvariant() + (ext.Length > 0 ? "." + ext : "");
    }

    /// <summary>The last segment, whichever separator the sender used. Both are stripped explicitly
    /// because <see cref="Path.GetFileName(string)"/> does not treat a backslash as a separator
    /// everywhere conductor runs.</summary>
    private static string Leaf(string name)
    {
        var cut = name.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? name.Trim() : name[(cut + 1)..].Trim();
    }

    /// <summary>Everything that is not plainly a filename character, gone. Deny-by-default rather
    /// than a blocklist of the separators we happen to have thought of, and capped in length so a
    /// 3,000-character name cannot make an unopenable path.</summary>
    private static string Scrub(string leaf)
    {
        var kept = new char[Math.Min(leaf.Length, 80)];
        var n = 0;
        foreach (var c in leaf)
        {
            if (n == kept.Length) break;
            var ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';
            kept[n++] = ok ? c : '_';
        }
        // A name that is only dots is a directory reference, not a file.
        var s = new string(kept, 0, n).Trim('.');
        return s;
    }

    /// <summary>The two refusal sentences, assembled where the messenger is known: the seam owns
    /// the SHAPE (name, reason, "your message was kept") and the adapter owns the reason itself.</summary>
    private static string TooBig(string fileName, long sizeBytes) =>
        InboundAck.Refused(fileName, TelegramLimits.TooBigReason(sizeBytes));

    private static string NotFetched(string fileName, string why) =>
        InboundAck.Refused(fileName, TelegramLimits.NotFetchedReason(why));

    /// <summary>The file-download root. <c>_apiBase</c> ends in <c>/bot</c>; downloads live one path
    /// segment sideways at <c>/file/bot&lt;token&gt;/</c>, which is why this is derived rather than
    /// concatenated onto it.</summary>
    private string FileBase => _apiBase[..^3] + "file/bot" + _token + "/";
}
