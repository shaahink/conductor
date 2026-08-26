using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Inbox;

/// <summary>
/// DV3.2 — the per-project inbox: a durable store for notes that arrive when the owner thinks of
/// them, and survive until a session reads them.
///
/// <para>It lives under <c>&lt;stateDir&gt;/inbox</c> and it is NEVER committed. That is not an
/// oversight to be fixed later: <c>.conductor/.gitignore</c> is deny-by-default with a short
/// allowlist, this repo is public, and an <c>!inbox/</c> entry would ship the owner's voice-note
/// transcripts to the world on the next push (findings §6.1). Nothing here writes a gitignore
/// entry, and a test pins its absence.</para>
///
/// <para><b>Two writers, one inbox</b> (findings §6.6). A courier files a note while a session reads
/// the battery, and both may be different processes. Three properties make that safe, and each is
/// carried by a mechanism rather than by a convention:</para>
/// <list type="number">
/// <item>a note file is written to a temp name and RENAMED into place, so a reader never sees a
///   half-written note;</item>
/// <item>the rename refuses to overwrite, which is what makes <c>update_id</c> a real dedup key: a
///   courier restart replays every update Telegram still holds (findings §6.2) and the second
///   filing of the same note loses the race by design, quietly;</item>
/// <item>the index is append-only and never rewritten, so no writer can lose another's line.</item>
/// </list>
///
/// <para>Nothing here deletes. A note that has been read is MARKED, not removed — the cursor is the
/// only thing that moves — so a mark that turns out to be wrong costs a re-read rather than a lost
/// message. Deletion is an explicit prune and lives elsewhere.</para>
/// </summary>
public sealed class InboxStore
{
    /// <summary>The directory name under the state dir. A constant because the gitignore test, the
    /// battery and the adapter all have to mean the same directory.</summary>
    public const string DirName = "inbox";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public InboxStore(string stateDir)
    {
        ArgumentNullException.ThrowIfNull(stateDir);
        StateDir = stateDir;
        Dir = Path.Combine(stateDir, DirName);
    }

    /// <summary>The project's <c>.conductor</c> — the inbox's parent. Kept rather than recomputed
    /// from <see cref="Dir"/> because DV4.4 writes a promoted followups row BESIDE the inbox, and
    /// walking back up a path to find the directory we were handed is how a trailing separator turns
    /// into a row written one level too high.</summary>
    public string StateDir { get; }

    /// <summary>The inbox root. Media written by the channel adapter lives beside the notes, under
    /// <c>media/</c>, which is why a transcript and its audio never drift apart.</summary>
    public string Dir { get; }

    public string NotesDir => Path.Combine(Dir, "notes");
    public string IndexPath => Path.Combine(Dir, "index.jsonl");
    public string CursorPath => Path.Combine(Dir, "cursor.json");

    /// <summary>Files one note. Returns false when a note with this id is ALREADY filed — the
    /// ordinary outcome of a courier restart, not an error, and the caller should say nothing.
    ///
    /// <para>The dedup is the atomic rename itself rather than a read-then-write check, because a
    /// check and a write are two operations and two writers fit between them.</para></summary>
    public bool Append(InboxNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        Directory.CreateDirectory(NotesDir);

        var target = NotePath(note.Id);
        var temp = target + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                 + "-" + Guid.NewGuid().ToString("N")[..8];

        File.WriteAllText(temp, JsonSerializer.Serialize(note, Json), new UTF8Encoding(false));
        try
        {
            // overwrite: false is the whole dedup. On every platform this repo runs on, a rename
            // onto an existing name fails rather than clobbering it.
            File.Move(temp, target, overwrite: false);
        }
        catch (IOException)
        {
            TryDelete(temp);
            return false;
        }

        AppendIndexLine(note);
        return true;
    }

    /// <summary>Every note ever filed, oldest id first. Reads the index, then folds in any note file
    /// the index does not mention — the crash window between the rename and the index append is
    /// real, and a note that exists on disk but is missing from the index would otherwise be
    /// invisible forever.</summary>
    /// <summary>Whether a note with this id is already filed. Cheap — one file-exists, no index read.
    ///
    /// <para>DV4.1: <see cref="Append"/>'s refusal to overwrite is still the DEDUP, and this is not a
    /// substitute for it — a check and a write are two operations and two writers fit between them.
    /// What this is for is everything a caller does BEFORE the write. The courier adopts a note's
    /// media into the inbox first, and on a replayed delivery that adoption ran, Append then refused,
    /// and the inbox kept an orphan audio file no note referenced and no prune could ever remove.</para></summary>
    public bool Has(long id) => File.Exists(NotePath(id));

    /// <summary>One note by id, or null. DV4.4 — a promote button carries an id and nothing else, so
    /// the press has to be able to find the note without reading the whole inbox back.</summary>
    public InboxNote? Find(long id)
    {
        var path = NotePath(id);
        return File.Exists(path) ? ReadNote(path) : null;
    }

    public IReadOnlyList<InboxNote> All()
    {
        if (!Directory.Exists(NotesDir)) return [];

        var byId = new Dictionary<long, InboxNote>();
        foreach (var file in Directory.EnumerateFiles(NotesDir, "*.json"))
        {
            if (ReadNote(file) is { } note) byId[note.Id] = note;
        }

        var indexed = IndexedIds();
        foreach (var id in byId.Keys)
            if (!indexed.Contains(id)) AppendIndexLine(byId[id]);   // repair, append-only

        return [.. byId.Values.OrderBy(n => n.Id)];
    }

    /// <summary>The notes no session has been handed yet.</summary>
    public IReadOnlyList<InboxNote> Unseen()
    {
        var through = ReadCursor().SeenThroughId;
        return [.. All().Where(n => n.Id > through)];
    }

    /// <summary>Marks every note up to and including <paramref name="throughId"/> as carried into a
    /// session's prompt. Idempotent, and it only ever moves FORWARD: two sessions composing at once
    /// cannot walk the cursor backwards and re-surface a note that has already been read.</summary>
    public void MarkSeen(long throughId, int sessionNumber)
    {
        var current = ReadCursor();
        if (throughId <= current.SeenThroughId) return;

        Directory.CreateDirectory(Dir);
        var cursor = new InboxCursor(throughId, sessionNumber, DateTime.UtcNow);
        var temp = CursorPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, JsonSerializer.Serialize(cursor, Json), new UTF8Encoding(false));
        try { File.Move(temp, CursorPath, overwrite: true); }
        catch (IOException) { TryDelete(temp); }
    }

    /// <summary>Where the cursor stands. A missing or unreadable cursor reads as FRESH — every note
    /// unseen — because the failure that loses a note is worse than the one that repeats it.</summary>
    public InboxCursor ReadCursor()
    {
        try
        {
            if (!File.Exists(CursorPath)) return InboxCursor.Fresh;
            return JsonSerializer.Deserialize<InboxCursor>(File.ReadAllText(CursorPath), Json)
                   ?? InboxCursor.Fresh;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return InboxCursor.Fresh;
        }
    }

    /// <summary>DV3.3 — attaches a transcript to a note that is ALREADY filed, and returns the note
    /// as it now stands (null when it has been pruned out from under us).
    ///
    /// <para>Filed first, transcribed second, on purpose. Transcription takes minutes for a long
    /// note and it can fail; doing it before the note existed would mean a machine that crashed
    /// mid-transcription lost the message entirely. Doing it after means the worst case is a note
    /// with its audio and no words — which is exactly the untranscribed state that is already a
    /// supported outcome.</para>
    ///
    /// <para>This is the ONE write here that overwrites: the dedup rename in <see cref="Append"/>
    /// refuses to, because a second DELIVERY of a note must not clobber the first. A transcript is
    /// not a second delivery, it is more of the same note, so it rewrites in place — atomically, so
    /// a reader mid-write still sees the untranscribed version rather than half a file.</para></summary>
    /// <param name="floor">The confidence below which a segment is marked — the plan's dial, passed
    /// in rather than read here, so the store has no opinion about anybody's model.</param>
    public InboxNote? AttachTranscript(long id, Transcript transcript, double floor)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var path = NotePath(id);
        if (ReadNote(path) is not { } note) return null;

        var relative = TranscriptRelPath(note);
        var sidecar = Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        AtomicFile.Write(sidecar, transcript.ToSidecarJson(floor));

        // A caption is the owner's TYPED words and the transcript is their spoken ones. Both are
        // theirs; neither replaces the other, so a captioned voice note keeps both, caption first.
        var marked = transcript.Marked(floor);
        var text = note.Text.Trim().Length > 0 ? note.Text.TrimEnd() + Environment.NewLine + marked : marked;

        var updated = note with
        {
            Text = text,
            TranscriptPath = relative,
            TranscriptConfidence = transcript.MeanConfidence,
        };

        AtomicFile.Write(path, JsonSerializer.Serialize(updated, Json));
        AppendIndexLine(updated);   // append-only: the LAST line for an id is the current one
        return updated;
    }

    /// <summary>DV3.4 — takes ownership of a media file, wherever it is now, and answers with the
    /// path THIS store records for it.
    ///
    /// <para>The channel downloads a file before anything knows which project the note belongs to,
    /// so a note routed to another project arrives with its audio sitting in the receiving run's
    /// inbox. Moving it is not tidiness: "the audio is kept beside the transcript" (findings §1.6) is
    /// false the moment the two live under different projects, and a prune of one would orphan the
    /// other.</para>
    ///
    /// <para>Already inside this store: returned as a relative path, untouched. Outside it: moved,
    /// and copied instead when the move fails (a different volume, a file someone else holds open) —
    /// losing the audio to tidy it away would be the worst trade in this system.</para></summary>
    /// <returns>The path to record on the note: relative to <see cref="Dir"/> when the file is
    /// inside it, the original absolute path when there is nothing to adopt, or null for no media.</returns>
    public string? AdoptMedia(string? path)
    {
        if (path is not { Length: > 0 }) return null;

        var root = Path.GetFullPath(Dir);
        var full = Path.GetFullPath(path);
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Relative(full, root);

        if (!File.Exists(full)) return path;   // nothing on disk to adopt; keep what we were told

        var mediaDir = Path.Combine(Dir, "media");
        Directory.CreateDirectory(mediaDir);
        var target = Unique(Path.Combine(mediaDir, Path.GetFileName(full)));

        try { File.Move(full, target); }
        catch (IOException)
        {
            try { File.Copy(full, target, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
        }
        catch (UnauthorizedAccessException) { return path; }

        return Relative(Path.GetFullPath(target), root);
    }

    private static string Relative(string full, string root) =>
        full[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A name nothing is using. Two projects can hold two different notes whose files came
    /// off the wire with the same name, and a silent overwrite here would be one owner's voice note
    /// replacing another's.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ext);
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    /// <summary>Where a note's transcript goes: beside its audio, under the same name. "Beside" is
    /// the requirement from findings §1.6 — the audio survives a garbled transcript only if a person
    /// moving one directory takes both.</summary>
    public static string TranscriptRelPath(InboxNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.MediaPath is { Length: > 0 } media
            ? media + ".transcript.json"
            : "notes/" + note.Id.ToString(CultureInfo.InvariantCulture) + ".transcript.json";
    }

    /// <summary>Every file this note owns that is actually on disk: the note, its media, its
    /// transcript. What a prune deletes, and what a prune PREVIEW prints — the same list from the
    /// same method, so the preview cannot promise a different set than the deletion takes.</summary>
    public IReadOnlyList<string> FilesOf(InboxNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var files = new List<string> { NotePath(note.Id) };
        foreach (var rel in new[] { note.MediaPath, note.TranscriptPath })
        {
            if (rel is not { Length: > 0 }) continue;
            var full = Path.IsPathRooted(rel)
                ? rel
                : Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            files.Add(full);
        }
        return [.. files.Where(File.Exists)];
    }

    /// <summary>DV3.3 / findings §6.1 — THE ONLY DELETION PATH IN THIS SYSTEM. Nothing else removes a
    /// note, its audio or its transcript: not reading it, not marking it seen, not a full disk, not a
    /// new run. Retention is a decision the owner makes by typing <c>conductor inbox prune</c>, which
    /// is the answer to open question 5 and the reason a note can be trusted to still be there.
    ///
    /// <para>A pruned id is recorded in the index rather than erased from it, so "where did note 47
    /// go" has an answer.</para></summary>
    /// <returns>How many files were actually removed.</returns>
    public int Prune(InboxNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var removed = 0;
        foreach (var file in FilesOf(note))
        {
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (removed > 0) AppendPrunedLine(note);
        return removed;
    }

    /// <summary>The audit line a prune leaves behind. Public for the same reason
    /// <see cref="AppendIndexLine"/> is: MA0045 exempts public members, and the honest answer to
    /// "this method does synchronous file IO" here is that the whole store is synchronous by design
    /// (the prompt-battery seam above it is a property), not that it should be hidden.</summary>
    public void AppendPrunedLine(InboxNote note)
    {
        var line = JsonSerializer.Serialize(new
        {
            id = note.Id,
            utc = DateTime.UtcNow,
            pruned = true,
            kind = note.Kind,
        }, Compact);

        AppendJsonLine(IndexPath, line);
    }

    private string NotePath(long id) =>
        Path.Combine(NotesDir, id.ToString(CultureInfo.InvariantCulture) + ".json");

    /// <summary>Reads one note file, or null when it is unreadable or not a note. Public and
    /// synchronous on purpose: the prompt-battery seam is synchronous all the way up
    /// (<c>IPromptBattery.Section</c> is a property), so an async store would only push a
    /// sync-over-async wait one layer out.</summary>
    public static InboxNote? ReadNote(string path)
    {
        try { return JsonSerializer.Deserialize<InboxNote>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One JSON object per line, appended, never rewritten. Retried a few times because a
    /// second writer holds the file for the microsecond its own append takes — losing an index line
    /// is survivable (<see cref="All"/> repairs it), but only if we tried.</summary>
    public void AppendIndexLine(InboxNote note)
    {
        var line = JsonSerializer.Serialize(new
        {
            id = note.Id,
            utc = note.ReceivedUtc,
            chat = note.ChatId,
            kind = note.Kind,
            file = "notes/" + note.Id.ToString(CultureInfo.InvariantCulture) + ".json",
            summary = note.Summary,
        }, Compact);

        AppendJsonLine(IndexPath, line);
    }

    /// <summary>Appends ONE whole line, or nothing at all.
    ///
    /// <para>The share mode is the entire point and it was wrong once. An append opened
    /// <c>FileShare.ReadWrite</c> lets a second writer in, and a .NET <c>FileStream</c> in append
    /// mode carries its OWN idea of where the end is — resolved when the handle opens, advanced by
    /// its own writes. Two handles open at the same length therefore write over each other rather
    /// than after each other, and what lands is not two lines or one line but a line spliced through
    /// the middle of another: <c>All</c> can still repair a MISSING line, and can do nothing at all
    /// with a corrupt one. Forty concurrent notes reproduced it every time.</para>
    ///
    /// <para>So the writer takes the file: <c>FileShare.Read</c> admits readers and refuses other
    /// writers, and the loser retries instead of interleaving. The append itself is a handle open and
    /// one small write, so the window it holds is microseconds and the backoff clears easily. If
    /// every attempt still loses, the line is dropped rather than risked — a missing index line is
    /// the failure this store is built to survive.</para>
    ///
    /// <para>Public for the same MA0045 public-member exemption as
    /// <see cref="AppendPrunedLine"/>.</para></summary>
    public static void AppendJsonLine(string path, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.Read, 4096);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) { Thread.Sleep(2 * (attempt + 1)); }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    /// <summary>Every line of a file that other writers may be APPENDING to right now.
    ///
    /// <para><c>File.ReadLines</c> would ask for <c>FileShare.Read</c>, which locks concurrent
    /// appenders out for the whole length of the scan and turns a reader into the thing that drops
    /// index lines. This asks for <c>FileShare.ReadWrite</c> instead, so a reader never costs a
    /// writer anything; the worst it can see is a line being written as it passes, and a half-read
    /// tail line is discarded by the JSON parse above rather than believed.</para>
    ///
    /// <para>Public for the same MA0045 public-member exemption as <see cref="ReadNote"/>: the whole
    /// store is synchronous by design, because the prompt-battery seam above it is a property.</para></summary>
    public static IReadOnlyList<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private HashSet<long> IndexedIds()
    {
        var ids = new HashSet<long>();
        if (!File.Exists(IndexPath)) return ids;
        try
        {
            foreach (var line in ReadLinesShared(IndexPath))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var v))
                        ids.Add(v);
                }
                catch (JsonException) { }   // a torn line is not a reason to lose the rest
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return ids;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
