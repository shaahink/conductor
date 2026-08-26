using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Conductor.Core.Integrations.Messaging;

using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>
/// DV4.1 — the Bot API's file half, extracted so there is exactly ONE of it.
///
/// <para>Every line here was <c>TelegramService.Inbound.cs</c>'s until the courier needed the same
/// thing. The courier (findings §1.4-B) owns the token on a machine where no run need be live, so it
/// cannot borrow a <see cref="TelegramService"/> — that class is built from a <c>PlanConfig</c> and
/// a <c>RunState</c> it has no business inventing. Copying two hundred lines of cap-enforcement and
/// path-scrubbing into a second poller would have been the version of this that fails quietly: the
/// 20 MB rule is checked in three places on purpose, and a second copy is a second place for one of
/// the three to go missing.</para>
///
/// <para>So the fetch is a component with a media directory, and the two callers differ only in
/// which directory that is: a run's <c>.conductor/inbox/media</c>, or the courier's machine-level
/// staging directory, whose contents <c>InboxStore.AdoptMedia</c> then moves into whichever project
/// the note routed to.</para>
///
/// <para>Behaviour is unchanged from DV3.1 — deliberately, to the byte. The DV3.1 wire tests drive
/// it through a stub Bot API and are the proof of that.</para>
/// </summary>
public sealed class TelegramMediaFetcher
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _token;
    private readonly Func<string> _mediaDir;
    private readonly ILogger _log;

    /// <param name="apiBase">The API root ending in <c>/bot</c> — <c>TelegramService</c>'s own shape,
    /// because the download root is derived from it sideways rather than concatenated onto it.</param>
    /// <param name="mediaDir">Resolved per call, not captured: a run reloads its plan and its state
    /// dir can move under it, and a fetcher holding yesterday's path would write there.</param>
    public TelegramMediaFetcher(HttpClient http, string apiBase, string token, Func<string> mediaDir,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(mediaDir);
        _http = http;
        _apiBase = apiBase;
        _token = token;
        _mediaDir = mediaDir;
        _log = log;
    }

    /// <summary>getFile, then the bytes. Every exit hands back an <see cref="InboundMedia"/> — one
    /// with a path when the download worked, one with a REASON when it did not. There is no exit
    /// that returns null and lets the message evaporate, which is the defect DV3.1 closed.</summary>
    public async Task<InboundMedia> FetchAsync(InboundMediaKind kind, TgFileRef file, long messageId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
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
            found = await _http.GetFromJsonAsync<TgFileResponse>(url, TelegramService.JsonOpts, ct)
                .ConfigureAwait(false);
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
        var dir = _mediaDir();
        var target = Path.Combine(dir, StoredName(kind, file, filePath, messageId));
        try
        {
            Directory.CreateDirectory(dir);
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

    /// <summary>An <see cref="InboundMedia"/> with no bytes and a reason — never a null, which is
    /// the shape that used to lose a message.</summary>
    public static InboundMedia Undownloaded(InboundMediaKind kind, TgFileRef file, string? refusal)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new(kind, file.FileId, DisplayName(kind, file), file.MimeType, file.FileSize ?? 0,
            file.Duration, null, refusal);
    }

    /// <summary>What to CALL it in a sentence to a human. A document has the sender's own name; the
    /// other three kinds have none on the wire, so they get the noun, which is what the sender would
    /// have called it anyway.</summary>
    public static string DisplayName(InboundMediaKind kind, TgFileRef file)
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
    public static string StoredName(InboundMediaKind kind, TgFileRef file, string filePath, long messageId)
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
