using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Store;

/// <summary>
/// K3.1: brings a pre-K3.1 <c>&lt;repo&gt;/.conductor/run.db</c> into the machine-level state home.
/// <para><b>Import, not move.</b> The legacy file is COPIED and left where it was. That is
/// deliberate: an older engine may still be installed on this machine (and, while this era is being
/// built, one of them is the engine driving the session), and renaming its database out from under
/// it would hand it a fresh empty store and look exactly like data loss. The copy is also the
/// backup — if the import is ever wrong, the original is still sitting there.</para>
/// <para><b>Idempotent, and never destructive.</b> Work written at the target after the import
/// survives every later resolution. A target that is still exactly the copy this made is the one
/// exception: if the legacy file has moved on since — the shape of an upgrade, where the old engine
/// keeps writing its own database for hours after a new build first resolved state — the copy is
/// refreshed rather than left behind (bug #33). A target that has a history of its own is never
/// touched, and the divergence is said out loud instead. See <see cref="ImportLegacy"/>.</para>
/// </summary>
public static class StateMigration
{
    /// <summary>Dropped next to the imported database as the durable record of what moved — the
    /// "says what it moved" half of the checkpoint, readable long after the stderr line scrolled.</summary>
    public const string ReceiptFileName = "imported.json";

    /// <summary>Where the one-line "imported this, from there" notice goes. The CLI installs a
    /// writer at startup (<c>Program</c>); the control plane, the Face and the test suite leave it
    /// null and read <see cref="ReadReceipt"/> or the catalogue instead. An import fires at most
    /// once per (repo, plan) — the destination existing is what stops it — so this is not chatter.</summary>
    public static Action<StateImport>? Announce { get; set; }

    /// <summary>Where a "the copy you are about to run from is behind its source" notice goes. Same
    /// posture as <see cref="Announce"/>: the store persists, it does not present (K2.2), so the CLI
    /// installs the writer and every other caller stays silent.</summary>
    public static Action<string>? Warn { get; set; }

    /// <summary>A one-line rendering of an import, so every surface says the same sentence.</summary>
    public static string Describe(StateImport i)
        => (i.Refreshed
                ? $"conductor: refreshed a stale copy of the run history -> {i.To} (re-copied from {i.From}, "
                : $"conductor: imported existing run history -> {i.To} (from {i.From}, ")
           + $"{i.Bytes} bytes, {i.Files.Count} file(s); the original was left in place)";

    /// <summary>SQLite side files. A database checkpointed cleanly has neither; one left behind by a
    /// crash has both, and copying the main file alone would silently drop the un-checkpointed
    /// tail.</summary>
    private static readonly string[] SideCarSuffixes = ["-wal", "-shm"];

    /// <summary>
    /// Copies <paramref name="legacyDb"/> to <paramref name="targetDb"/> if the legacy file exists
    /// and the target does not — or if the target is provably still the untouched copy this made and
    /// the legacy file has moved on since (bug #33, see <see cref="IsStaleSnapshotOf"/>). Returns
    /// null when there was nothing to import (the ordinary case for a fresh repo, and for every
    /// resolution after the first).
    /// <para>KS0.1: <paramref name="stateRoot"/> is what makes "after the first" mean the first time
    /// on this MACHINE rather than the first time under this PLAN SLUG. Without it the destination
    /// existing is the only test, and the destination is keyed on the slug — so a new plan in an old
    /// repo imports the same database again and every run in it is listed twice. Passing the root
    /// lets <see cref="StateDedup.FindPriorImport"/> ask the question by run id instead. Null keeps
    /// the old behaviour, which is what the tests that are about copying alone want.</para>
    /// </summary>
    public static StateImport? ImportLegacy(string legacyDb, string targetDb, string? stateRoot = null)
    {
        try
        {
            if (!File.Exists(legacyDb)) return null;
            if (PathsEqual(legacyDb, targetDb)) return null;

            var refreshing = false;
            if (File.Exists(targetDb))
            {
                if (!IsStaleSnapshotOf(legacyDb, targetDb, out var behind))
                {
                    // Behind, but not safely refreshable: something has been written at the target
                    // since the copy, so both files hold work and only a human can say which wins.
                    // Silence is the one answer that is certainly wrong.
                    if (behind)
                        Warn?.Invoke($"conductor: {targetDb} is a COPY of {legacyDb} taken earlier, and the "
                                     + "original has been written since — this run resumes from the copy. Both hold "
                                     + "work; reconcile them before trusting either.");
                    return null;
                }
                refreshing = true;
            }

            // KS0.1. Nothing is at the target, so K3.1 would copy — but the question it was asking is
            // "has this slug seen the file", and the question that matters is "has this MACHINE seen
            // these runs". A copy under a second slug does not add history; it adds a second listing
            // of history that is already here, which is how 25 runs became 37 rows.
            if (!refreshing && stateRoot is { Length: > 0 }
                && StateDedup.FindPriorImport(stateRoot, legacyDb, targetDb) is { } prior)
            {
                Warn?.Invoke(StateDedup.Describe(prior, Path.GetFullPath(legacyDb)));
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDb)!);

            // Copy the sidecars FIRST, then the main file. A reader that arrives mid-import then
            // either sees no main file at all (and treats the target as absent) or sees a main file
            // whose -wal is already beside it. The reverse order can expose a torn database.
            var copied = new List<string>();
            foreach (var suffix in SideCarSuffixes)
            {
                var src = legacyDb + suffix;
                if (!File.Exists(src))
                {
                    // A refresh can find the sidecars of the copy it is replacing still lying there.
                    // Pairing a fresh main file with a stale -wal is how a database gets torn.
                    if (refreshing) TryDelete(targetDb + suffix);
                    continue;
                }
                File.Copy(src, targetDb + suffix, overwrite: true);
                copied.Add(Path.GetFileName(src));
            }

            var tmp = targetDb + ".importing";
            File.Copy(legacyDb, tmp, overwrite: true);
            File.Move(tmp, targetDb, overwrite: refreshing);
            copied.Insert(0, Path.GetFileName(legacyDb));

            var import = new StateImport(
                From: Path.GetFullPath(legacyDb),
                To: Path.GetFullPath(targetDb),
                Bytes: new FileInfo(targetDb).Length,
                Files: copied,
                ImportedAtUtc: DateTimeOffset.UtcNow,
                Refreshed: refreshing);

            // The durable half of "says what it moved" — written inline because this synchronous
            // public boundary owns the blocking I/O, and a receipt that failed to write must not
            // undo an import that succeeded.
            try
            {
                File.WriteAllText(ReceiptPathFor(targetDb), JsonSerializer.Serialize(import, ReceiptOpts));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            // The immediate half. The store persists, it does not present (K2.2), so the notice goes
            // through a sink the shell installs: nothing is printed when nobody is listening.
            Announce?.Invoke(import);
            return import;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A failed import must not take the CLI down: the caller falls through to a fresh
            // database at the target, and the legacy file is untouched and still importable. It must
            // not be invisible either — "your history is somewhere else" is exactly the sentence a
            // silent failure here costs (bug #33's whole shape).
            TryCleanup(targetDb);
            Warn?.Invoke($"conductor: could not bring {legacyDb} to {targetDb} ({e.Message}); "
                         + "the original is untouched and still importable.");
            return null;
        }
    }

    /// <summary>
    /// Bug #33. The import fires once and the target is authoritative afterwards — which is right
    /// until the OLD engine keeps writing the legacy file after the copy was taken. That is precisely
    /// the shape of an upgrade on this project: a new build resolves state once and leaves a snapshot,
    /// the published engine goes on running the same run for hours against
    /// <c>&lt;repo&gt;/.conductor/run.db</c>, and then the install makes the snapshot the live
    /// database. The run silently resumes from where it stood at that first resolution, and every
    /// session since is gone from its own history.
    /// <para>The question is asked of CONTENT, through
    /// <see cref="SqliteRunStore.CompareHistories"/>, because timestamps and sizes cannot answer it
    /// and were measured not answering it: the engine migrates the copy's schema the instant it lands,
    /// so the file is "modified" before anything of substance has happened to it, and a legacy
    /// database in WAL mode can gain a whole session while its main file and its sidecar's write time
    /// both sit still. A copy whose history is a subset of the source's may be replaced, because
    /// nothing is in it that is not also in the source. A copy holding history of its own is never
    /// touched, and <paramref name="behind"/> asks for that to be said out loud rather than resumed
    /// from in silence.</para>
    /// </summary>
    private static bool IsStaleSnapshotOf(string legacyDb, string targetDb, out bool behind)
    {
        behind = false;

        // A database this never imported is not a snapshot of anything — it is somebody's state.
        if (ReadReceipt(targetDb) is not { } receipt) return false;
        if (!PathsEqual(receipt.From, legacyDb)) return false;

        // KS0.1: a copy the repair pass has deduplicated is no longer a copy of anything. Its history
        // is deliberately a subset of the source's — that is what the repair DID — so the content
        // rule below would read "SourceAhead" and re-import every row the repair removed, and the
        // duplicates would grow back the next time this repo resolved.
        if (receipt.RepairedAtUtc is not null) return false;

        // Take the file-level facts BEFORE asking the content question. Opening a WAL database
        // read-only creates a -shm beside it, so the very act of reading the copy makes the copy look
        // written-to; measured, and it cost an afternoon.
        var importedAt = receipt.ImportedAtUtc.UtcDateTime;
        var legacy = new FileInfo(legacyDb);
        var target = new FileInfo(targetDb);
        var sourceMoved = legacy.Length != receipt.Bytes
                          || legacy.LastWriteTimeUtc > importedAt
                          || SideCarSuffixes.Any(s => WrittenSince(legacyDb + s, importedAt));
        var copyUntouched = target.Length == receipt.Bytes
                            && target.LastWriteTimeUtc <= importedAt
                            && !SideCarSuffixes.Any(s => WrittenSince(targetDb + s, importedAt));

        switch (SqliteRunStore.CompareHistories(targetDb, legacyDb))
        {
            case SqliteRunStore.HistoryRelation.SourceAhead:
                return true;                       // the copy has nothing of its own to lose
            case SqliteRunStore.HistoryRelation.Diverged:
                behind = true;                     // both moved: a person has to reconcile them
                return false;
            case SqliteRunStore.HistoryRelation.Same:
            case SqliteRunStore.HistoryRelation.CopyAhead:
                return false;                      // the ordinary states, and both are quiet ones
        }

        // Neither file answers as a run database. Fall back to the file-level fence, which is
        // stricter than the content rule and never wrong in the destructive direction: refresh only
        // an untouched copy of a file that has since changed.
        behind = sourceMoved;
        return sourceMoved && copyUntouched;
    }

    private static bool WrittenSince(string path, DateTime utc)
    {
        var f = new FileInfo(path);
        return f.Exists && f.LastWriteTimeUtc > utc;
    }

    /// <summary>
    /// KS0.1: stamps the receipt beside a store the repair pass has just deduplicated. Two jobs, both
    /// load-bearing: it records that the store is no longer the copy its receipt describes (so bug
    /// #33's refresh cannot undo the repair — see <see cref="IsStaleSnapshotOf"/>), and it leaves the
    /// provenance intact so the store can still say where it came from. False when there was no
    /// receipt to stamp, which is not an error: a store that was never imported has nothing to undo.
    /// </summary>
    public static bool MarkRepaired(string targetDb, DateTimeOffset at)
    {
        if (ReadReceipt(targetDb) is not { } receipt) return false;
        try
        {
            File.WriteAllText(ReceiptPathFor(targetDb),
                JsonSerializer.Serialize(receipt with { RepairedAtUtc = at }, ReceiptOpts));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads the receipt beside an imported database, if there is one.</summary>
    public static StateImport? ReadReceipt(string targetDb)
    {
        try
        {
            var p = ReceiptPathFor(targetDb);
            return File.Exists(p)
                ? JsonSerializer.Deserialize<StateImport>(File.ReadAllText(p), ReceiptOpts)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string ReceiptPathFor(string targetDb)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetDb))!, ReceiptFileName);

    private static readonly JsonSerializerOptions ReceiptOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void TryCleanup(string targetDb) => TryDelete(targetDb + ".importing");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>What a legacy import moved. Serialised to <c>imported.json</c> beside the database and
/// echoed into the catalogue entry.</summary>
/// <param name="From">The legacy database, which still exists.</param>
/// <param name="To">The imported copy, now authoritative.</param>
/// <param name="Bytes">Size of the imported main file.</param>
/// <param name="Files">Every file copied, main first, sidecars after.</param>
/// <param name="ImportedAtUtc">When.</param>
/// <param name="Refreshed">True when this replaced an earlier copy that the source had outrun
/// (bug #33) rather than landing on empty ground. Absent from receipts written before that existed,
/// which deserialise as false — correct, since those were first imports.</param>
/// <param name="RepairedAtUtc">KS0.1. Set when the repair pass removed runs from this store because
/// another store owns them. From that moment the store is deliberately NOT a copy of
/// <paramref name="From"/> any more, and bug #33's refresh must leave it alone.</param>
public sealed record StateImport(
    string From,
    string To,
    long Bytes,
    IReadOnlyList<string> Files,
    DateTimeOffset ImportedAtUtc,
    bool Refreshed = false,
    DateTimeOffset? RepairedAtUtc = null);
