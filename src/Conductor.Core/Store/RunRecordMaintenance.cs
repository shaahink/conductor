using System.Globalization;

using Conductor.Core.Events;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Core.Store;

                               // files, driven by a CLI command that has nothing else to do
                               // meanwhile. Same posture as StateRepair.cs.

/// <summary>
/// KS0.2 — the record half of read-side truthfulness: closing and annotating a <c>runs</c> row from
/// outside the engine that wrote it.
/// <para>Until this checkpoint there was no way to do either. An engine that is killed — and they are
/// killed: window closed, machine rebooted, background task reaped with its parent session — never
/// gets to write its own ending, so the row says <c>running</c> for ever. Four such rows sit in this
/// machine's catalogue today, and the last one that had to be corrected was corrected the only way
/// there was: <b>hand-edited SQL in two databases</b>, a procedure written down in
/// <c>.conductor/WATCH-HANDOFF.md</c> for the next person to repeat. This class is what retires that
/// procedure, and the reason the retirement matters is not convenience — hand SQL takes no backup,
/// checks no liveness, leaves no provenance, and cannot be tested.</para>
/// <para>Three rules, inherited from <see cref="StateRepair"/> because they were paid for there:
/// a store a live engine is using is never written; a run is identified by its <b>run id</b> and not
/// by which store it happens to sit in; and whatever is changed says who changed it and why, in the
/// event spine, where it outlives this process.</para>
/// </summary>
public static class RunRecordMaintenance
{
    /// <summary>The event kind a record change is journalled under.</summary>
    public const string NoteKind = "record";

    // ────────────────────────────────────────────────────────────────────────────────── find

    /// <summary>Every catalogued store that holds a run whose id starts with
    /// <paramref name="idOrPrefix"/>. Read-only. A prefix is allowed because run ids are printed
    /// short everywhere an operator would read one; more than one match is the caller's problem to
    /// report, never this method's to guess at.</summary>
    public static IReadOnlyList<RunRecordMatch> Find(string root, string idOrPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrPrefix);
        var survey = StateRepair.Survey(root);
        var matches = new List<RunRecordMatch>();
        foreach (var store in survey.Stores)
            foreach (var run in store.Runs)
                if (run.RunId.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
                    matches.Add(new RunRecordMatch(run.RunId, run.PlanName, run.Status, run.StartedUtc,
                                                   store.Db, store.Slug, store.Live));
        return matches;
    }

    /// <summary>The last moment this run demonstrably did anything, across its events and its
    /// sessions. This is what a closure should stamp as <c>ended_utc</c>: a run that died on
    /// 5 August did not end today because someone noticed today, and the difference is every
    /// duration and cost-per-hour figure computed from the row afterwards. Null when the run left
    /// nothing behind but its own row.</summary>
    public static DateTimeOffset? LastActivityUtc(string dbPath, string runId)
    {
        try
        {
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT MAX(t) FROM (" +
                "  SELECT MAX(ts) AS t FROM events WHERE run_id = @runId" +
                "  UNION ALL SELECT MAX(ended_utc) FROM sessions WHERE run_id = @runId" +
                "  UNION ALL SELECT MAX(started_utc) FROM sessions WHERE run_id = @runId)";
            cmd.Parameters.AddWithValue("@runId", runId);
            var raw = cmd.ExecuteScalar();
            if (raw is not string s || s.Length == 0) return null;
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                           out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────── write

    /// <summary>Close a record: a terminal status, the instant it really stopped, and a note in the
    /// spine saying who did it and why.</summary>
    public static RunRecordOutcome Close(
        RunRecordMatch match, string status, DateTimeOffset endedUtc, string by, string? reason, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(clock);
        if (!RunRecord.IsTerminal(status))
            return RunRecordOutcome.Refused(
                $"'{status}' is not a closed state - use one of {string.Join(", ", RunRecord.CloseableAs)}");
        if (match.Live)
            return RunRecordOutcome.Refused(LiveRefusal(match));

        var note = $"closed: {match.Status} -> {status}, ended {endedUtc.ToString("O", CultureInfo.InvariantCulture)}"
                   + Because(reason);
        return Write(match, by, note, clock,
                     store => store.CloseRunRecord(match.RunId, status, endedUtc) > 0);
    }

    /// <summary>Annotate a record without touching its lifecycle: the run is someone's again, and the
    /// spine says whose. The status is deliberately untouched — a record you have adopted is one you
    /// intend to keep, which is the opposite of closing it.</summary>
    public static RunRecordOutcome Adopt(RunRecordMatch match, string by, string note, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        ArgumentNullException.ThrowIfNull(clock);
        if (match.Live)
            return RunRecordOutcome.Refused(LiveRefusal(match));

        return Write(match, by, $"adopted: {note}", clock, _ => true);
    }

    private static string Because(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "" : $". reason: {reason.Trim()}";

    private static string LiveRefusal(RunRecordMatch m) =>
        $"{Short(m.RunId)} lives in {m.Slug}, which a live engine is using - a record is never changed "
        + "under the engine that owns it. Stop that run (or wait for it) and try again.";

    /// <summary>Open the one store, apply the change, journal it, close. Everything that touches a
    /// store on disk goes through here so that the note and the row can never disagree about whether
    /// the change happened: the note is only written when <paramref name="apply"/> reports it
    /// did.</summary>
    private static RunRecordOutcome Write(
        RunRecordMatch match, string by, string note, TimeProvider clock, Func<SqliteRunStore, bool> apply)
    {
        SqliteRunStore? store = null;
        try
        {
            store = new SqliteRunStore(match.Db, NullLogger<SqliteRunStore>.Instance, clock);
            if (!apply(store))
                return RunRecordOutcome.Refused($"{Short(match.RunId)} is not in {match.Slug} any more - nothing written");

            var line = $"{note}. by {by} at {clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}";
            // Emit stamps the RunId from the store's own field, overwriting whatever the event
            // carries — so the run has to be named here or the note lands under the empty run id and
            // is invisible to every reader of the run it describes.
            store.SetRunId(match.RunId);
            store.Emit(new NoteAdded { RunId = match.RunId, Kind = NoteKind, Content = line });
            store.FlushEvents();
            return RunRecordOutcome.Done(line);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return RunRecordOutcome.Refused($"{match.Db} could not be written: {ex.Message}");
        }
        finally
        {
            store?.Dispose();
        }
    }

    private static string Short(string runId) => runId[..Math.Min(8, runId.Length)];
}

/// <summary>One run row, in one store, as the maintenance verbs found it.</summary>
/// <param name="Live">An engine is using this store. Nothing here will write it.</param>
public sealed record RunRecordMatch(
    string RunId,
    string PlanName,
    string Status,
    string StartedUtc,
    string Db,
    string Slug,
    bool Live);

/// <summary>What a record change did, or why it did not happen. There is no exception path for
/// "refused": a refusal is an ordinary answer here and the CLI prints it as one.</summary>
public sealed record RunRecordOutcome(bool Ok, string Message)
{
    public static RunRecordOutcome Done(string message) => new(true, message);
    public static RunRecordOutcome Refused(string message) => new(false, message);
}
