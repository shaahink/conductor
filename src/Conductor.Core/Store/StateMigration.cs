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
/// <para><b>Idempotent.</b> A target that already exists is never touched: the import only fires
/// when the destination is absent, so a row written after the import survives every later
/// resolution. See <see cref="ImportLegacy"/>.</para>
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
    /// </summary>
    public static StateImport? ImportLegacy(string legacyDb, string targetDb)
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
            // database at the target, and the legacy file is untouched and still importable.
            TryCleanup(targetDb);
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
    /// <para>Refreshing is only safe when the target is provably STILL the copy this made — same
    /// size, no write to it or its sidecars since the receipt — and the legacy file has moved on.
    /// Then re-copying can lose nothing, because nothing exists at the target that is not also in the
    /// source. Every other shape (no receipt, a different origin, a target that has been written)
    /// keeps the old answer and never touches it; <paramref name="behind"/> reports the case that
    /// deserves a warning instead — the copy is stale AND carries work of its own.</para>
    /// </summary>
    private static bool IsStaleSnapshotOf(string legacyDb, string targetDb, out bool behind)
    {
        behind = false;

        // A database this never imported is not a snapshot of anything — it is somebody's state.
        if (ReadReceipt(targetDb) is not { } receipt) return false;
        if (!PathsEqual(receipt.From, legacyDb)) return false;

        var importedAt = receipt.ImportedAtUtc.UtcDateTime;
        var legacy = new FileInfo(legacyDb);

        // Has the source moved on? File.Copy carries the source's write time across, so the receipt's
        // instant is a clean fence for both files. A live SQLite database in WAL mode grows its
        // sidecar long before the main file changes, so the sidecars count as writes too.
        behind = legacy.Length != receipt.Bytes
                 || legacy.LastWriteTimeUtc > importedAt
                 || SideCarSuffixes.Any(s => WrittenSince(legacyDb + s, importedAt));
        if (!behind) return false;

        var target = new FileInfo(targetDb);
        return target.Length == receipt.Bytes
               && target.LastWriteTimeUtc <= importedAt
               && !SideCarSuffixes.Any(s => WrittenSince(targetDb + s, importedAt));
    }

    private static bool WrittenSince(string path, DateTime utc)
    {
        var f = new FileInfo(path);
        return f.Exists && f.LastWriteTimeUtc > utc;
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

    private static bool PathsEqual(string a, string b)
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
public sealed record StateImport(
    string From,
    string To,
    long Bytes,
    IReadOnlyList<string> Files,
    DateTimeOffset ImportedAtUtc,
    bool Refreshed = false);
