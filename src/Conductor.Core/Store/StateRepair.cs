using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Conductor.Core.Store;

#pragma warning disable MA0045 // Sync DB access by design — a one-shot maintenance pass over local
                               // files, driven by a CLI command that has nothing else to do meanwhile.
                               // Same posture as SqliteRunStore.cs.

/// <summary>
/// KS0.1, the second half: <see cref="StateDedup"/> stops new duplicates being minted, and this
/// collapses the ones already on disk.
/// <para><b>Three rules, and they are the whole design.</b></para>
/// <para><b>1. Back up before writing anything.</b> Every store the pass will touch is copied whole —
/// database, sidecars and receipt — before the first DELETE runs. The repair is the only thing in
/// this engine that removes history, so the cost of being wrong has to be a restore, not a loss.</para>
/// <para><b>2. Never write a store a live engine is using.</b> This machine runs more than one
/// conductor at a time by design. A duplicate held by a live store is left there and removed from the
/// idle copies instead — which is also why the ownership rule prefers the live store: the safe
/// choice and the correct one are the same choice.</para>
/// <para><b>3. A run's identity is its run id.</b> Which store a row sits in is an accident of which
/// plan happened to resolve first; the run id is the thing that is duplicated and the thing that is
/// counted.</para>
/// </summary>
public static class StateRepair
{
    /// <summary>Where backups go, under the state home so a restore never has to hunt.</summary>
    public const string BackupsDirName = "backups";

    private static readonly string[] SideCarSuffixes = ["-wal", "-shm"];

    // ─────────────────────────────────────────────────────────────────────────────── survey

    /// <summary>Reads every store this machine has and works out what is duplicated. Read-only: it
    /// takes no write lock and leaves no sidecar, so it is safe to run against a live machine and is
    /// what the dry run prints.</summary>
    public static RepairPlan Survey(string root)
    {
        var catalogue = StateCatalogue.Read(root);
        var stores = new List<StoreSurvey>();
        var unreadable = new List<string>();

        foreach (var db in StateDedup.Stores(root))
        {
            var runs = ReadRuns(db);
            if (runs is null)
            {
                unreadable.Add($"{db} does not answer as a run database; left alone");
                continue;
            }

            var entry = catalogue.FirstOrDefault(e => StateMigration.PathsEqual(e.RunDb, db));
            stores.Add(new StoreSurvey(
                Db: db,
                Slug: entry?.Slug is { Length: > 0 } s ? s : Path.GetFileName(Path.GetDirectoryName(db)!) ?? db,
                Plan: entry?.Plan ?? "",
                FirstSeenUtc: entry?.FirstSeenUtc ?? DateTimeOffset.MaxValue,
                Live: LooksLive(db),
                Runs: runs));
        }

        var duplicates = new List<DuplicateRun>();
        var deferred = new List<string>(unreadable);

        var grouped = stores
            .SelectMany(s => s.Runs.Select(r => (Store: s, Run: r)))
            .GroupBy(x => x.Run.RunId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var g in grouped)
        {
            var candidates = g.Select(x => x.Store).ToList();
            var planName = g.First().Run.PlanName;
            var live = candidates.Where(c => c.Live).ToList();

            if (live.Count > 1)
            {
                deferred.Add($"{Short(g.Key)} is in {candidates.Count} stores and {live.Count} of them are "
                             + "in use by a live engine; not touching either - re-run when one has finished");
                continue;
            }

            var (owner, why) = live.Count == 1
                ? (live[0], "a live engine is using it")
                : candidates.FirstOrDefault(c => string.Equals(c.Plan, planName, StringComparison.Ordinal)) is { } home
                    ? (home, "it is that plan's own store")
                    : (candidates.OrderBy(c => c.FirstSeenUtc)
                                 .ThenBy(c => c.Db, StringComparer.Ordinal).First(), "it was the first import");

            duplicates.Add(new DuplicateRun(
                RunId: g.Key,
                PlanName: planName,
                OwnerDb: owner.Db,
                OwnerReason: why,
                RemoveFrom: candidates.Where(c => !StateMigration.PathsEqual(c.Db, owner.Db))
                                      .Select(c => c.Db).ToList()));
        }

        var rows = stores.Sum(s => s.Runs.Count);
        var distinct = stores.SelectMany(s => s.Runs.Select(r => r.RunId))
                             .Distinct(StringComparer.Ordinal).Count();
        return new RepairPlan(root, stores, rows, distinct, duplicates, deferred);
    }

    // ──────────────────────────────────────────────────────────────────────────────── apply

    /// <summary>
    /// Backs up every store the plan will change, then removes the duplicated runs from all but their
    /// owner. Throws <see cref="InvalidOperationException"/> rather than write a store a live engine
    /// is using — <see cref="Survey"/> never plans that, so reaching it means the machine changed
    /// under the pass and stopping is the honest answer.
    /// </summary>
    public static RepairOutcome Apply(string root, RepairPlan plan, DateTimeOffset now)
    {
        var byStore = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in plan.Duplicates)
            foreach (var db in d.RemoveFrom)
            {
                if (!byStore.TryGetValue(db, out var ids)) byStore[db] = ids = [];
                ids.Add(d.RunId);
            }

        var notes = new List<string>();
        if (byStore.Count == 0)
            return new RepairOutcome("", [], 0, ["nothing duplicated; nothing written"]);

        foreach (var db in byStore.Keys)
            if (plan.Stores.Any(s => StateMigration.PathsEqual(s.Db, db) && s.Live))
                throw new InvalidOperationException(
                    $"refusing to write {db}: a live engine is using it");

        // Every backup first, then every write. A pass that backed up store 1, wrote store 1, and
        // then failed to back up store 2 would leave the operator holding half a safety net.
        var backupDir = Path.Combine(root, BackupsDirName,
            "repair-" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDir);
        foreach (var db in byStore.Keys) BackUp(db, backupDir);
        CopyIfExists(StateHome.CataloguePathFor(root), Path.Combine(backupDir, StateCatalogue.FileName));

        var changed = new List<string>();
        var rows = 0;
        foreach (var (db, ids) in byStore)
        {
            using var c = new SqliteConnection($"Data Source={db}");
            c.Open();
            using (var pragma = c.CreateCommand())
            {
                // Another engine may be reading this store even though none is running it.
                pragma.CommandText = "PRAGMA busy_timeout=15000;";
                pragma.ExecuteNonQuery();
            }

            using var tx = c.BeginTransaction();
            var tables = RunIdTables(c, tx);
            foreach (var id in ids) rows += DeleteRun(c, tx, tables, id);
            tx.Commit();

            changed.Add(db);
            // The store is no longer the copy its receipt describes. Without this the next resolution
            // reads it as "behind its source" and copies every removed row back (bug #33's refresh).
            if (StateMigration.MarkRepaired(db, now))
                notes.Add($"{Path.GetFileName(Path.GetDirectoryName(db)!)}: receipt stamped repaired");
        }

        return new RepairOutcome(backupDir, changed, rows, notes);
    }

    // ────────────────────────────────────────────────────────────────────────────── plumbing

    /// <summary>Every table that carries a <c>run_id</c>, <c>runs</c> last. Asked of the schema rather
    /// than hard-coded because this store has gained tables in four migrations already, and a repair
    /// that misses one leaves orphaned rows that no surface will ever show again. <c>runs</c> goes
    /// last because foreign keys ARE enforced here (Microsoft.Data.Sqlite turns them on) — measured,
    /// by a test that deleted the parent first and got SQLITE_CONSTRAINT.</summary>
    public static IReadOnlyList<string> RunIdTables(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT m.name FROM sqlite_master m
            WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
              AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE name = 'run_id')
            ORDER BY (m.name = 'runs'), m.name
            """;
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));
        return tables;
    }

    /// <summary>Removes every trace of one run from one store, and says how many rows went.</summary>
    public static int DeleteRun(SqliteConnection c, SqliteTransaction? tx, IReadOnlyList<string> tables, string runId)
    {
        var rows = 0;
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            // The table name comes from sqlite_master, never from a caller; the run id is a parameter.
            cmd.CommandText = $"DELETE FROM \"{t}\" WHERE run_id = $id";
            cmd.Parameters.AddWithValue("$id", runId);
            rows += cmd.ExecuteNonQuery();
        }
        return rows;
    }

    /// <summary>The runs in a store. Null when the file will not answer as a run database.</summary>
    private static IReadOnlyList<StoreRun>? ReadRuns(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return null;
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT run_id, plan_name, status, started_utc FROM runs ORDER BY started_utc";
            using var r = cmd.ExecuteReader();
            var runs = new List<StoreRun>();
            while (r.Read())
                runs.Add(new StoreRun(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return runs;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Is an engine using this store right now? A run row still saying <c>running</c> proves
    /// nothing — four of those on this machine are engines that exited without closing the record,
    /// which is KS0.2's whole subject. A tracked pid that is still alive proves it.</summary>
    private static bool LooksLive(string dbPath)
    {
        try
        {
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT p.pid, p.started_utc FROM pids p
                JOIN runs r ON r.run_id = p.run_id
                WHERE r.status = 'running' AND p.exited_utc IS NULL
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (PidLiveness.LooksAlive((int)r.GetInt64(0), SqliteRunStore.ParseUtc(r.GetString(1))))
                    return true;
            return false;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private static void BackUp(string db, string backupDir)
    {
        var dir = Path.Combine(backupDir, Path.GetFileName(Path.GetDirectoryName(db)!) ?? "store");
        Directory.CreateDirectory(dir);
        File.Copy(db, Path.Combine(dir, Path.GetFileName(db)), overwrite: true);
        foreach (var s in SideCarSuffixes) CopyIfExists(db + s, Path.Combine(dir, Path.GetFileName(db) + s));
        CopyIfExists(StateMigration.ReceiptPathFor(db), Path.Combine(dir, StateMigration.ReceiptFileName));
    }

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Copy(from, to, overwrite: true);
    }

    private static string Short(string runId) => runId[..Math.Min(8, runId.Length)];
}

/// <summary>One run row as the repair reads it.</summary>
public sealed record StoreRun(string RunId, string PlanName, string Status, string StartedUtc);

/// <summary>One catalogued store, and whether an engine is using it.</summary>
public sealed record StoreSurvey(
    string Db,
    string Slug,
    string Plan,
    DateTimeOffset FirstSeenUtc,
    bool Live,
    IReadOnlyList<StoreRun> Runs);

/// <summary>A run that exists in more than one store, and where it is going to live.</summary>
/// <param name="OwnerDb">The store that keeps it.</param>
/// <param name="OwnerReason">Why that one — printed, because an operator about to delete history is
/// owed the reasoning rather than a verdict.</param>
/// <param name="RemoveFrom">The stores it is removed from.</param>
public sealed record DuplicateRun(
    string RunId,
    string PlanName,
    string OwnerDb,
    string OwnerReason,
    IReadOnlyList<string> RemoveFrom);

/// <summary>What the pass found. Produced read-only, so this is exactly what the dry run prints and
/// exactly what <see cref="StateRepair.Apply"/> acts on.</summary>
public sealed record RepairPlan(
    string Root,
    IReadOnlyList<StoreSurvey> Stores,
    int RunRows,
    int DistinctRuns,
    IReadOnlyList<DuplicateRun> Duplicates,
    IReadOnlyList<string> Deferred);

/// <summary>What the pass did.</summary>
public sealed record RepairOutcome(
    string BackupDir,
    IReadOnlyList<string> StoresChanged,
    int RowsDeleted,
    IReadOnlyList<string> Notes);
