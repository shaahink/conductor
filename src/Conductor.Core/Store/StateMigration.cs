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

    /// <summary>A one-line rendering of an import, so every surface says the same sentence.</summary>
    public static string Describe(StateImport i)
        => $"conductor: imported existing run history -> {i.To} (from {i.From}, {i.Bytes} bytes, "
           + $"{i.Files.Count} file(s); the original was left in place)";

    /// <summary>SQLite side files. A database checkpointed cleanly has neither; one left behind by a
    /// crash has both, and copying the main file alone would silently drop the un-checkpointed
    /// tail.</summary>
    private static readonly string[] SideCarSuffixes = ["-wal", "-shm"];

    /// <summary>
    /// Copies <paramref name="legacyDb"/> to <paramref name="targetDb"/> if the legacy file exists
    /// and the target does not. Returns null when there was nothing to import (the ordinary case
    /// for a fresh repo, and for every resolution after the first).
    /// </summary>
    public static StateImport? ImportLegacy(string legacyDb, string targetDb)
    {
        try
        {
            if (File.Exists(targetDb)) return null;
            if (!File.Exists(legacyDb)) return null;
            if (PathsEqual(legacyDb, targetDb)) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(targetDb)!);

            // Copy the sidecars FIRST, then the main file. A reader that arrives mid-import then
            // either sees no main file at all (and treats the target as absent) or sees a main file
            // whose -wal is already beside it. The reverse order can expose a torn database.
            var copied = new List<string>();
            foreach (var suffix in SideCarSuffixes)
            {
                var src = legacyDb + suffix;
                if (!File.Exists(src)) continue;
                File.Copy(src, targetDb + suffix, overwrite: true);
                copied.Add(Path.GetFileName(src));
            }

            var tmp = targetDb + ".importing";
            File.Copy(legacyDb, tmp, overwrite: true);
            File.Move(tmp, targetDb, overwrite: false);
            copied.Insert(0, Path.GetFileName(legacyDb));

            var import = new StateImport(
                From: Path.GetFullPath(legacyDb),
                To: Path.GetFullPath(targetDb),
                Bytes: new FileInfo(targetDb).Length,
                Files: copied,
                ImportedAtUtc: DateTimeOffset.UtcNow);

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

    private static void TryCleanup(string targetDb)
    {
        foreach (var p in new[] { targetDb + ".importing" })
            try { if (File.Exists(p)) File.Delete(p); }
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
public sealed record StateImport(
    string From,
    string To,
    long Bytes,
    IReadOnlyList<string> Files,
    DateTimeOffset ImportedAtUtc);
