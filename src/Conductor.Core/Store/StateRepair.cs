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
/// <para><b>4. A copy is only removed when the copy being kept CONTAINS it</b>, proved by set
/// containment over that run's sessions and events (<see cref="RunKeys"/>). Copies of one legacy
/// database are not interchangeable: K3.1 moved run.db to the state home, so a run went on writing
/// into its own slug store while the legacy path froze, and every later import of that frozen file
/// holds a truncated copy. This rule exists because the first version of this pass did not have it,
/// kept the copy that was two events short, and lost a confirmed checkpoint — recovered from the
/// backup rule 1 had already taken.</para>
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
                // KS1.3: the liveness rule this pass got right the expensive way now lives in
                // RunLiveness, so the listing surfaces reconcile by the SAME rule the repair
                // refuses to write against. One rule, two callers, no second opinion.
                Live: RunLiveness.StoreLooksLive(db, entry?.Repo),
                Foreign: !IsUnder(root, db),
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

            // Copies of one legacy database are NOT interchangeable, and assuming they were cost this
            // checkpoint a confirmed checkpoint. K3.1 moved run.db to the state home, so a run kept
            // writing into ITS OWN slug store while the legacy path froze - and every later import of
            // that frozen file holds a TRUNCATED copy of it. Measured on this machine: five copies of
            // karvan-core, four with 3722 events and one, its own store, with 3724.
            var keys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var unreadableCopy = false;
            foreach (var c in candidates)
            {
                var k = RunKeys(c.Db, g.Key);
                if (k is null) { unreadableCopy = true; break; }
                keys[c.Db] = k;
            }
            if (unreadableCopy)
            {
                deferred.Add($"{Short(g.Key)}: one of its {candidates.Count} copies will not say what it "
                             + "holds, so no copy can be proved redundant; left alone");
                continue;
            }

            // The keeper must CONTAIN every copy it replaces. Anything else is deleting rows that
            // exist nowhere else, whatever the ownership rule says.
            var maximal = candidates
                .Where(c => candidates.All(o => keys[o.Db].IsSubsetOf(keys[c.Db])))
                .ToList();
            if (maximal.Count == 0)
            {
                deferred.Add($"{Short(g.Key)}: its {candidates.Count} copies have DIVERGED - each holds "
                             + "sessions or events the others do not, so none is redundant. A person has "
                             + "to merge them; nothing removed");
                continue;
            }

            // Every maximal copy holds exactly the same rows, so which one keeps them is free - and a
            // free choice should go to the store that is safest to leave alone.
            var live = maximal.Where(c => c.Live).ToList();
            var (owner, why) = live.Count > 0
                ? (live[0], "a live engine is using it, and its copy is complete")
                : maximal.FirstOrDefault(c => string.Equals(c.Plan, planName, StringComparison.Ordinal)) is { } home
                    ? (home, "it is that plan's own store, and holds the fullest copy")
                    : (maximal.OrderBy(c => c.FirstSeenUtc)
                              .ThenBy(c => c.Db, StringComparer.Ordinal).First(),
                       "it holds the fullest copy, and was the first import");

            var losers = candidates.Where(c => !StateMigration.PathsEqual(c.Db, owner.Db)).ToList();

            // A live store is never written - not even to remove a row the keeper already has. This
            // engine sets no busy_timeout, so a write lock held here is a SQLITE_BUSY in somebody
            // else's run. Their copy is redundant and stays; the line below says when it can go.
            var busy = losers.Where(c => c.Live).ToList();
            foreach (var l in busy)
                deferred.Add($"{Short(g.Key)} is also in {l.Slug}, which a live engine is using. Its copy "
                             + "holds nothing the keeper does not - re-run this once that engine has "
                             + "finished and it will go");
            losers = losers.Where(c => !c.Live).ToList();

            // A home is repaired within its own walls. A catalogue is a list of absolute paths and
            // nothing stops one pointing outside the home that holds it — a copied state home points
            // at the ORIGINAL machine's stores, which is exactly how a rehearsal on a copy would
            // quietly delete rows from the live home. Measured, on the copy that was meant to be safe.
            var outside = losers.Where(c => c.Foreign).ToList();
            foreach (var o in outside)
                deferred.Add($"{Short(g.Key)} is also in {o.Db}, which is outside this state home; "
                             + "not touching it - repair that home from its own root");

            duplicates.Add(new DuplicateRun(
                RunId: g.Key,
                PlanName: planName,
                OwnerDb: owner.Db,
                OwnerReason: why,
                RemoveFrom: losers.Where(c => !c.Foreign).Select(c => c.Db).ToList()));
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
        {
            if (plan.Stores.Any(s => StateMigration.PathsEqual(s.Db, db) && s.Live))
                throw new InvalidOperationException(
                    $"refusing to write {db}: a live engine is using it");
            if (!IsUnder(root, db))
                throw new InvalidOperationException(
                    $"refusing to write {db}: it is outside the state home being repaired ({root})");
        }

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

    /// <summary>
    /// Identity keys for ONE run inside one store — its session numbers and its event sequence
    /// numbers, the same two tables <see cref="SqliteRunStore.CompareHistories"/> compares whole
    /// databases by, narrowed to a single run. Null when the store will not answer.
    /// <para>This is what makes the pass lossless: a copy may only be removed when the copy being
    /// kept is a superset of it. Counts would not do — two copies can hold the same NUMBER of events
    /// and different events — so it is set containment or nothing.</para>
    /// </summary>
    private static HashSet<string>? RunKeys(string dbPath, string runId)
    {
        try
        {
            using var c = StateDedup.OpenReadOnly(dbPath);
            c.Open();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var answered = ReadRunKeys(c, "SELECT number FROM sessions WHERE run_id = $id", "s", runId, keys)
                           | ReadRunKeys(c, "SELECT seq FROM events WHERE run_id = $id", "e", runId, keys);
            return answered ? keys : null;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ReadRunKeys(SqliteConnection c, string sql, string prefix, string runId,
        HashSet<string> into)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", runId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) into.Add($"{prefix}:{r.GetInt64(0)}");
            return true;
        }
        catch (SqliteException)
        {
            return false;   // no such table in this schema version
        }
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

    /// <summary>Is this store inside the home being repaired? The one containment rule the pass has,
    /// and the reason a rehearsal on a copied state home cannot reach back into the real one.</summary>
    internal static bool IsUnder(string root, string path)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(r,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }
}
