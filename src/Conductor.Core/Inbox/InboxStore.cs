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
        Dir = Path.Combine(stateDir, DirName);
    }

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
    /// second writer may hold the file for the microsecond its own append takes — losing an index
    /// line is survivable (<see cref="All"/> repairs it), but only if we tried.</summary>
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

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(IndexPath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite, 4096);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) { Thread.Sleep(5 * (attempt + 1)); }
        }
    }

    private HashSet<long> IndexedIds()
    {
        var ids = new HashSet<long>();
        if (!File.Exists(IndexPath)) return ids;
        try
        {
            foreach (var line in File.ReadLines(IndexPath))
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
