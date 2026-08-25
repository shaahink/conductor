using System.Globalization;

using Conductor.Core;
using Conductor.Core.Inbox;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV3.2 — the per-project inbox: does a note survive the run that received it, does it survive a
/// SECOND delivery of itself, and does it stop being surfaced once a session has read it.
///
/// <para>Findings §6.6 named the miss these pin: §1.7 said where a note lands and never said when
/// it stops being carried, so without a cursor the battery grows without bound for any long-lived
/// project. And §6.2 named the other: a courier restart replays every update the messenger still
/// holds, so "file it" has to mean "file it once".</para>
/// </summary>
public sealed class DV3_2InboxStoreTests : IDisposable
{
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;

    public DV3_2InboxStoreTests(ITestOutputHelper output)
    {
        _out = output;
        _stateDir = Path.Combine(Path.GetTempPath(), $"conductor-dv32-{Guid.NewGuid():N}", ".conductor");
        Directory.CreateDirectory(_stateDir);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_stateDir)!.FullName); } catch (Exception) { }
    }

    private InboxStore Store() => new(_stateDir);

    /// <summary>The id an index line names, or null if the line is not whole JSON — which is how a
    /// torn interleaved append would announce itself.</summary>
    private static long? IdOf(string line)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var v) ? v : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static InboxNote Note(long id, string text, string kind = "voice", string? media = null) =>
        new(id, new DateTime(2026, 8, 25, 21, 4, 0, DateTimeKind.Utc), "99205495", kind, text, media);

    // ── it survives, and it survives being delivered twice ──

    /// <summary>The note is on disk, in its own file, readable by anything that can read JSON — and
    /// the index has exactly one line for it.</summary>
    [Fact]
    public void A_filed_note_is_a_file_on_disk_and_a_line_in_the_index()
    {
        var store = Store();
        Assert.True(store.Append(Note(11, "the login flow is broken on mobile")));

        var noteFile = Path.Combine(store.NotesDir, "11.json");
        Assert.True(File.Exists(noteFile), noteFile);
        _out.WriteLine(File.ReadAllText(noteFile));

        var index = File.ReadAllLines(store.IndexPath);
        var line = Assert.Single(index);
        Assert.Contains("\"id\":11", line, StringComparison.Ordinal);
        Assert.Contains("the login flow is broken on mobile", line, StringComparison.Ordinal);

        var back = Assert.Single(store.All());
        Assert.Equal("the login flow is broken on mobile", back.Text);
        Assert.Equal("voice", back.Kind);
    }

    /// <summary>Findings §6.2 — the courier restarts and Telegram hands it the same update again.
    /// The second filing is refused, the note is not duplicated, and the index gains no line.</summary>
    [Fact]
    public void The_same_update_id_filed_twice_lands_once()
    {
        var store = Store();
        Assert.True(store.Append(Note(11, "first delivery")));
        Assert.False(store.Append(Note(11, "the SAME update, replayed after a restart")));

        Assert.Single(store.All());
        Assert.Single(File.ReadAllLines(store.IndexPath));
        // The first text won: a replay must not overwrite what was already recorded.
        Assert.Equal("first delivery", store.All()[0].Text);
        // And no temp file was left behind by the losing writer.
        Assert.Empty(Directory.GetFiles(store.NotesDir, "*.tmp-*"));
    }

    /// <summary>Two writers, one inbox (findings §6.6) — a courier and a run filing at once. Every
    /// distinct note lands exactly once and the index has one line each, with no torn lines.</summary>
    [Fact]
    public async Task Concurrent_writers_neither_lose_a_note_nor_duplicate_one()
    {
        var store = Store();
        var ids = Enumerable.Range(1, 40).Select(i => (long)i).ToList();

        // Each id is offered by TWO writers at once: the winner files it, the loser is refused.
        var filed = await Task.WhenAll(ids.SelectMany(id => new[]
        {
            Task.Run(() => Store().Append(Note(id, "note " + id.ToString(CultureInfo.InvariantCulture)))),
            Task.Run(() => Store().Append(Note(id, "duplicate of " + id.ToString(CultureInfo.InvariantCulture)))),
        }));

        Assert.Equal(ids.Count, filed.Count(ok => ok));
        Assert.Equal(ids.Count, store.All().Count);

        // Every line is whole JSON with an id — an interleaved append would show up here — and every
        // note is named at least once. The index is APPEND-ONLY and best-effort by design: under
        // contention a line can be written twice (a repair racing a slow append), and that costs a
        // duplicate line, not a duplicate note. The dedup that matters is the atomic rename of the
        // note file, asserted by the All() count above.
        var lines = (await File.ReadAllLinesAsync(store.IndexPath)).Where(l => l.Length > 0).ToList();
        var indexed = lines.Select(IdOf).ToList();
        Assert.DoesNotContain(indexed, id => id is null);
        Assert.Equal(ids.Count, indexed.Distinct().Count());
        Assert.Empty(Directory.GetFiles(store.NotesDir, "*.tmp-*"));
    }

    /// <summary>The crash window between the atomic rename and the index append is real. A note file
    /// with no index line must not be invisible — <c>All</c> folds it back in and repairs the index,
    /// append-only.</summary>
    [Fact]
    public void A_note_whose_index_line_was_lost_is_still_read_and_the_index_is_repaired()
    {
        var store = Store();
        store.Append(Note(11, "indexed normally"));

        // Exactly the crash: the note file exists, the index does not know about it.
        Directory.CreateDirectory(store.NotesDir);
        File.WriteAllText(Path.Combine(store.NotesDir, "12.json"),
            """{"Id":12,"ReceivedUtc":"2026-08-25T21:10:00Z","ChatId":"99205495","Kind":"text","Text":"the orphan"}""");

        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, n => n.Text == "the orphan");
        Assert.Equal(2, File.ReadAllLines(store.IndexPath).Count(l => l.Length > 0));
    }

    // ── the read cursor ──

    /// <summary>A cursor, not a delete: the notes are still on disk after being read, they are just
    /// no longer UNSEEN. And the cursor records which session took delivery.</summary>
    [Fact]
    public void The_cursor_moves_over_what_was_read_and_deletes_nothing()
    {
        var store = Store();
        store.Append(Note(11, "one"));
        store.Append(Note(12, "two"));
        store.Append(Note(13, "three"));
        Assert.Equal(3, store.Unseen().Count);

        store.MarkSeen(12, sessionNumber: 6);

        Assert.Equal(3, store.All().Count);                       // nothing deleted
        var unseen = Assert.Single(store.Unseen());
        Assert.Equal(13, unseen.Id);
        Assert.Equal(6, store.ReadCursor().SessionNumber);
    }

    /// <summary>The cursor only moves FORWARD. Two sessions composing at once must not be able to
    /// walk it backwards and re-surface a note that has already been read.</summary>
    [Fact]
    public void The_cursor_never_goes_backwards()
    {
        var store = Store();
        store.Append(Note(11, "one"));
        store.Append(Note(12, "two"));

        store.MarkSeen(12, 6);
        store.MarkSeen(11, 7);        // a straggler with a stale view

        Assert.Equal(12, store.ReadCursor().SeenThroughId);
        Assert.Empty(store.Unseen());
    }

    /// <summary>An unreadable cursor reads as FRESH. The failure that repeats a note is survivable;
    /// the one that loses it is not.</summary>
    [Fact]
    public void A_corrupt_cursor_reads_as_nothing_seen_rather_than_everything_seen()
    {
        var store = Store();
        store.Append(Note(11, "one"));
        store.MarkSeen(11, 6);
        Assert.Empty(store.Unseen());

        File.WriteAllText(store.CursorPath, "{ this is not json");

        Assert.Equal(InboxCursor.Fresh.SeenThroughId, store.ReadCursor().SeenThroughId);
        Assert.Single(store.Unseen());
    }

    // ── the battery: verbatim, capped, and the rest COUNTED ──

    /// <summary>Findings §6.6 — the notes that do not fit are counted in one line, not dropped in
    /// silence, and they stay unread so the next session gets them.</summary>
    [Fact]
    public void The_battery_carries_the_oldest_notes_verbatim_and_counts_the_rest()
    {
        var store = Store();
        for (var i = 1; i <= 5; i++)
            store.Append(Note(i, "note number " + i.ToString(CultureInfo.InvariantCulture)));

        var battery = new InboxBattery(store, maxNotes: 2);
        _out.WriteLine(battery.Section);

        Assert.Equal(5, battery.UnseenCount);
        Assert.Equal(2, battery.HighestSurfacedId);
        Assert.Contains("note number 1", battery.Section, StringComparison.Ordinal);
        Assert.Contains("note number 2", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("note number 3", battery.Section, StringComparison.Ordinal);
        Assert.Contains("3 more unread note(s) are NOT carried here", battery.Section, StringComparison.Ordinal);
    }

    /// <summary>A note too long for the section is clipped, and the clip is ANNOUNCED. A silent clip
    /// reads as the owner having said less than they did.</summary>
    [Fact]
    public void A_clipped_note_says_it_was_clipped()
    {
        var store = Store();
        store.Append(Note(11, new string('x', 900) + "THE-TAIL"));

        var battery = new InboxBattery(store, maxNotes: 1, maxChars: 100);
        Assert.Contains("CLIPPED", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("THE-TAIL", battery.Section, StringComparison.Ordinal);
    }

    /// <summary>An empty inbox contributes nothing at all — no heading over an ellipsis, no cursor
    /// move, no tokens spent.</summary>
    [Fact]
    public void An_empty_inbox_is_an_empty_battery()
    {
        var battery = new InboxBattery(Store());
        Assert.True(battery.IsEmpty);
        Assert.Equal("", battery.Section);
        Assert.Equal(0, battery.HighestSurfacedId);
    }
}
