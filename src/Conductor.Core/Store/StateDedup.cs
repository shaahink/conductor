using Microsoft.Data.Sqlite;

namespace Conductor.Core.Store;

/// <summary>
/// KS0.1: the guard that stops this machine's history from growing copies of itself.
/// <para>K3.1's import fires when a (repo, plan) pair resolves its state home for the first time, and
/// its only "have I done this already?" test was <b>the destination existing</b> — which is keyed on
/// the plan slug. So every NEW plan in an OLD repo imported that repo's whole legacy database again,
/// under a new slug, and <see cref="History.RunHistory"/> — which walks every catalogued store —
/// listed every run in it once more. Measured on 2026-08-13: one
/// <c>C:\code\conductor\.conductor\run.db</c> living in five stores, 37 run rows for 25 real runs,
/// and payesh's harvest refusing to run over the collision.</para>
/// <para><b>The identity of a run is its run id, never the slug it was imported under.</b> That is
/// the whole fix: before importing, ask what runs the legacy file holds and whether this machine
/// already remembers them — through the receipts (<c>imported.json</c>) first, because they are the
/// durable record of what moved, then the catalogue, and finally through the run ids themselves,
/// which are the only evidence that survives a deleted receipt and a rebuilt index.</para>
/// </summary>
public static class StateDedup
{
    /// <summary>Every run id in a database. Null when the file will not answer as a run database —
    /// absent, not a database, or too old to have a <c>runs</c> table. Read-only and pooling-free for
    /// the same reason <see cref="SqliteRunStore.CompareHistories"/> is: this may be pointed at a
    /// store another engine is writing, and asking a question must not take a lock or leave a
    /// <c>-wal</c> behind.</summary>
    public static IReadOnlyList<string>? RunIds(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return null;
            using var c = OpenReadOnly(dbPath);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT run_id FROM runs";
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A read-only, pooling-free connection to a run database. Shared with
    /// <see cref="StateRepair"/> so both surfaces read stores the same careful way.</summary>
    internal static SqliteConnection OpenReadOnly(string dbPath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    /// <summary>Every run store this machine has, catalogue first and then the disk beneath it. The
    /// catalogue is an index and indexes go missing (K3.1 says so itself), so the directory sweep is
    /// what makes the answer true rather than merely indexed.</summary>
    public static IReadOnlyList<string> Stores(string root)
    {
        var seen = new List<string>();
        void Add(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            string full;
            try { full = Path.GetFullPath(p); } catch (ArgumentException) { return; }
            if (!seen.Any(x => StateMigration.PathsEqual(x, full))) seen.Add(full);
        }

        foreach (var e in StateCatalogue.Read(root)) Add(e.RunDb);
        try
        {
            var runs = Path.Combine(root, StateHome.RunsDirName);
            if (Directory.Exists(runs))
                foreach (var d in Directory.EnumerateDirectories(runs))
                {
                    var db = Path.Combine(d, StateHome.RunDbFileName);
                    if (File.Exists(db)) Add(db);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return seen;
    }

    /// <summary>
    /// Has <paramref name="legacyDb"/> already been brought into this state home, somewhere other
    /// than <paramref name="targetDb"/>? Null means no, and the caller should import.
    /// </summary>
    public static PriorImport? FindPriorImport(string root, string legacyDb, string targetDb)
    {
        var stores = Stores(root)
            .Where(s => !StateMigration.PathsEqual(s, targetDb))
            .ToList();
        if (stores.Count == 0) return null;

        // The receipt is the durable record of what moved and it names its source, so it answers
        // first — and it answers even for a store whose database is currently unreadable.
        string? via = null;
        var evidence = PriorImportEvidence.Receipt;
        DateTimeOffset? at = null;
        foreach (var s in stores)
        {
            if (StateMigration.ReadReceipt(s) is not { } rec) continue;
            if (!StateMigration.PathsEqual(rec.From, legacyDb)) continue;
            via = s;
            at = rec.ImportedAtUtc;
            break;
        }

        if (via is null)
            foreach (var e in StateCatalogue.Read(root))
            {
                if (string.IsNullOrEmpty(e.ImportedFrom)) continue;
                if (!StateMigration.PathsEqual(e.ImportedFrom, legacyDb)) continue;
                if (StateMigration.PathsEqual(e.RunDb, targetDb) || !File.Exists(e.RunDb)) continue;
                via = Path.GetFullPath(e.RunDb);
                evidence = PriorImportEvidence.Catalogue;
                at = e.ImportedAtUtc;
                break;
            }

        // The run ids: the evidence that outlives a deleted receipt and a rebuilt catalogue, and the
        // only one that is about the runs rather than about the paperwork.
        var legacyRuns = RunIds(legacyDb);
        IReadOnlyList<string> missing = [];
        if (legacyRuns is { Count: > 0 })
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var holders = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in stores)
                foreach (var id in RunIds(s) ?? [])
                {
                    present.Add(id);
                    holders.TryAdd(id, s);
                }

            missing = legacyRuns.Where(id => !present.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
            if (via is null && missing.Count == 0)
            {
                via = holders[legacyRuns[0]];
                evidence = PriorImportEvidence.RunIds;
            }
        }

        return via is null ? null : new PriorImport(via, evidence, at, missing);
    }

    /// <summary>The one sentence every surface says about a skipped import. Loud when the legacy file
    /// holds runs this machine has never seen, because that is the one shape a person has to look
    /// at: copying would duplicate everything else in the file, and skipping leaves those runs
    /// where they are.</summary>
    public static string Describe(PriorImport prior, string legacyDb)
    {
        var when = prior.ImportedAtUtc is { } t ? $" on {t.UtcDateTime:yyyy-MM-dd}" : "";
        var how = prior.Evidence switch
        {
            PriorImportEvidence.Receipt => $"{StateMigration.ReceiptFileName} beside it says so",
            PriorImportEvidence.Catalogue => "the catalogue says so",
            _ => "its runs are already there",
        };
        return prior.MissingRunIds.Count == 0
            ? $"conductor: {legacyDb} was already imported to {prior.TargetDb}{when} ({how}); not importing "
              + "it again - a second copy would list every run in it twice."
            : $"conductor: {legacyDb} was already imported to {prior.TargetDb}{when} ({how}), but it holds "
              + $"{prior.MissingRunIds.Count} run(s) this machine has no record of "
              + $"({string.Join(", ", prior.MissingRunIds.Select(i => i[..Math.Min(8, i.Length)]))}). "
              + "NOT importing it again - a second copy would duplicate every run it already holds. "
              + "Reconcile by hand if those runs matter.";
    }
}

/// <summary>How this machine knows a legacy database has already been imported.</summary>
public enum PriorImportEvidence
{
    /// <summary>An <c>imported.json</c> beside another store names this file as its source.</summary>
    Receipt,
    /// <summary>A catalogue entry names this file as its source.</summary>
    Catalogue,
    /// <summary>No paperwork, but every run in the file is already in the machine's history.</summary>
    RunIds,
}

/// <summary>An earlier import of the same legacy database.</summary>
/// <param name="TargetDb">The store that already holds it.</param>
/// <param name="Evidence">What answered the question.</param>
/// <param name="ImportedAtUtc">When, if the paperwork said.</param>
/// <param name="MissingRunIds">Runs in the legacy file that exist nowhere else in this state home.
/// Empty is the ordinary case and the quiet one.</param>
public sealed record PriorImport(
    string TargetDb,
    PriorImportEvidence Evidence,
    DateTimeOffset? ImportedAtUtc,
    IReadOnlyList<string> MissingRunIds);
