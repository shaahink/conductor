using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS0.1 — the repair pass that collapses the duplicates already on disk.
///
/// <para>Every fixture here reproduces the real defect the honest way: it imports one legacy database
/// under two plan slugs through the PRE-FIX call (<see cref="StateMigration.ImportLegacy(string,
/// string)"/> without a state root), which is exactly what the shipped engine did on the operator's
/// machine until this checkpoint. So the thing being repaired is the thing that happened.</para>
/// </summary>
[Collection(StateSinkCollection.Name)]
public sealed class KS0_1CatalogueRepairTests : IDisposable
{
    private readonly string _tmp;

    public KS0_1CatalogueRepairTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks01r-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        StateMigration.Warn = null;
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Root => Path.Combine(_tmp, "home");

    private string NewRepo(string name)
    {
        var p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.Combine(p, StateHome.ScratchDirName));
        return p;
    }

    private static void SeedLegacy(string path, string planName, params string[] runIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new SqliteRunStore(path, NullLogger<SqliteRunStore>.Instance);
        foreach (var runId in runIds)
        {
            store.SetRunId(runId);
            store.InitializeRun(runId, planName, Path.GetDirectoryName(path)!, "main",
                Conductor.Core.EngineStamp.Parse("test"));
            store.InitializeStage(runId, "K1", "Stage One");
            store.RecordSession(runId, "K1", 7, "session",
                DateTime.UtcNow, null, null, null, 0, 1, null, null, 0, null);
        }
    }

    /// <summary>The defect, reproduced: the same legacy database copied under a second plan slug and
    /// catalogued, precisely as the pre-KS0.1 engine did it.</summary>
    private string ImportAgainTheOldWay(string repo, string plan)
    {
        var legacy = StateHome.LegacyDbPathFor(repo);
        var target = StateHome.DerivedRunDbPath(Root, repo, plan);
        var import = StateMigration.ImportLegacy(legacy, target);
        Assert.NotNull(import);
        StateCatalogue.Upsert(Root, repo, plan, target, import);
        return target;
    }

    private static IReadOnlyList<string> RunIdsAt(string db) => StateDedup.RunIds(db) ?? [];

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── the headline ───────────────────────────────────────────────────────────────────────────

    /// <summary>One run in two stores becomes one run in one store, and the machine's row count comes
    /// back down to its real run count.</summary>
    [Fact]
    public void ADuplicatedRun_EndsUpInExactlyOneStore()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha", "run-beta");
        var firstDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");

        var before = StateRepair.Survey(Root);
        Assert.Equal(4, before.RunRows);
        Assert.Equal(2, before.DistinctRuns);
        Assert.Equal(2, before.Duplicates.Count);

        var outcome = StateRepair.Apply(Root, before, DateTimeOffset.UtcNow);

        var after = StateRepair.Survey(Root);
        Assert.Equal(2, after.RunRows);
        Assert.Equal(2, after.DistinctRuns);
        Assert.Empty(after.Duplicates);
        Assert.True(outcome.RowsDeleted > 2, "the runs' sessions and stages go with them, not just the run row");

        // The owner is the store of the plan that owns the runs; the copy keeps nothing.
        Assert.Equal(2, RunIdsAt(firstDb).Count);
        Assert.Empty(RunIdsAt(secondDb));
    }

    /// <summary>The backup exists before a row is removed, and it holds what was removed. This is the
    /// only place in the engine that deletes history: if the pass is ever wrong, the cost has to be a
    /// restore.</summary>
    [Fact]
    public void TheBackupHoldsWhatWasRemoved()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        StateHome.Resolve(repo, "core plan", Root);
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");

        var outcome = StateRepair.Apply(Root, StateRepair.Survey(Root), DateTimeOffset.UtcNow);

        Assert.True(Directory.Exists(outcome.BackupDir));
        var backedUp = Directory.EnumerateFiles(outcome.BackupDir, StateHome.RunDbFileName,
            SearchOption.AllDirectories).ToList();
        var copy = Assert.Single(backedUp);
        Assert.Equal("run-alpha", Assert.Single(RunIdsAt(copy)));
        Assert.Empty(RunIdsAt(secondDb));
        Assert.True(File.Exists(Path.Combine(outcome.BackupDir, StateCatalogue.FileName)),
            "the index is backed up with the stores it indexes");
    }

    /// <summary>A store an engine is using is never written — and is preferred as the owner, so the
    /// safe answer and the correct one are the same answer. This machine runs two conductors at once
    /// by design; a repair that wrote a live store would be a way to break somebody else's run.</summary>
    [Fact]
    public void ALiveStoreIsChosenAsOwnerAndNeverWritten()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        var firstDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");

        // Make the SECOND store the live one — the one the ownership rule would otherwise reject,
        // so that a pass which ignored liveness would fail this test rather than pass it by luck.
        var me = System.Diagnostics.Process.GetCurrentProcess();
        Exec(secondDb, "UPDATE runs SET status = 'running' WHERE run_id = 'run-alpha'");
        using (var c = new SqliteConnection($"Data Source={secondDb}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO pids(pid, purpose, started_utc, run_id) VALUES($p, 'test', $s, 'run-alpha')";
            cmd.Parameters.AddWithValue("$p", me.Id);
            cmd.Parameters.AddWithValue("$s", me.StartTime.ToUniversalTime().ToString("o"));
            cmd.ExecuteNonQuery();
        }

        var plan = StateRepair.Survey(Root);
        var dup = Assert.Single(plan.Duplicates);

        Assert.Equal(Path.GetFullPath(secondDb), dup.OwnerDb);
        Assert.Contains("live engine", dup.OwnerReason, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(firstDb), Assert.Single(dup.RemoveFrom));

        StateRepair.Apply(Root, plan, DateTimeOffset.UtcNow);
        Assert.Equal("run-alpha", Assert.Single(RunIdsAt(secondDb)));
        Assert.Empty(RunIdsAt(firstDb));
    }

    /// <summary>Apply refuses rather than write a live store, even if it is handed a plan that says
    /// to. The survey never produces one; reaching this means the machine changed under the pass.</summary>
    [Fact]
    public void ApplyRefusesAPlanThatWouldWriteALiveStore()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        var firstDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");

        var plan = StateRepair.Survey(Root);
        var live = plan.Stores.Select(s =>
            StateMigration.PathsEqual(s.Db, firstDb) ? s with { Live = true } : s).ToList();
        var forged = plan with
        {
            Stores = live,
            Duplicates = [new DuplicateRun("run-alpha", "core plan", Path.GetFullPath(secondDb),
                "forged", [Path.GetFullPath(firstDb)])],
        };

        Assert.Throws<InvalidOperationException>(() => StateRepair.Apply(Root, forged, DateTimeOffset.UtcNow));
        Assert.Equal("run-alpha", Assert.Single(RunIdsAt(firstDb)));
    }

    /// <summary>Ownership, when nothing is live: the run goes to the store of its own plan, not to
    /// whichever store happened to be imported first.</summary>
    [Fact]
    public void OwnershipPrefersThePlansOwnStore()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "edge plan", "run-alpha");
        StateHome.Resolve(repo, "core plan", Root);          // imported FIRST, but not its plan
        var edgeDb = ImportAgainTheOldWay(repo, "edge plan"); // imported second, and it is its plan

        var dup = Assert.Single(StateRepair.Survey(Root).Duplicates);

        Assert.Equal(Path.GetFullPath(edgeDb), dup.OwnerDb);
        Assert.Contains("own store", dup.OwnerReason, StringComparison.Ordinal);
    }

    /// <summary>...and when no store's plan claims it, the earliest import keeps it, so the answer is
    /// the same whichever order the stores are read in.</summary>
    [Fact]
    public void OwnershipFallsBackToTheEarliestImport()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "a plan nobody catalogues", "run-alpha");
        var firstDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        ImportAgainTheOldWay(repo, "edge plan");

        var dup = Assert.Single(StateRepair.Survey(Root).Duplicates);

        Assert.Equal(Path.GetFullPath(firstDb), dup.OwnerDb);
        Assert.Contains("first import", dup.OwnerReason, StringComparison.Ordinal);
    }

    /// <summary>The repaired store's receipt is stamped, so bug #33's refresh cannot read the repair
    /// as "you are behind your source" and copy every removed row back in.</summary>
    [Fact]
    public void TheRepairedStoreIsStampedAndStaysRepaired()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        StateHome.Resolve(repo, "core plan", Root);
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");

        StateRepair.Apply(Root, StateRepair.Survey(Root), DateTimeOffset.UtcNow);

        var receipt = StateMigration.ReadReceipt(secondDb);
        Assert.NotNull(receipt);
        Assert.NotNull(receipt!.RepairedAtUtc);

        // and a resolution of that same slug does not undo it
        Assert.Null(StateHome.Resolve(repo, "edge plan", Root).Import);
        Assert.Empty(RunIdsAt(secondDb));
    }

    /// <summary>A clean machine is left completely alone: no backup directory, no writes, no
    /// surprises. A repair that "tidies" a store with nothing wrong with it is a repair nobody can
    /// afford to run.</summary>
    [Fact]
    public void ACleanMachineIsNotTouched()
    {
        var repo = NewRepo("clean-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        StateHome.Resolve(repo, "core plan", Root);

        var plan = StateRepair.Survey(Root);
        Assert.Empty(plan.Duplicates);
        Assert.Equal(plan.RunRows, plan.DistinctRuns);

        var outcome = StateRepair.Apply(Root, plan, DateTimeOffset.UtcNow);

        Assert.Equal(0, outcome.RowsDeleted);
        Assert.Empty(outcome.StoresChanged);
        Assert.False(Directory.Exists(Path.Combine(Root, StateRepair.BackupsDirName)));
    }

    /// <summary>A store that holds a duplicate AND a run of its own keeps its own run. The repair
    /// removes duplicated runs, not stores.</summary>
    [Fact]
    public void AStoreKeepsTheRunsThatAreOnlyItsOwn()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        StateHome.Resolve(repo, "core plan", Root);
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");
        SeedLegacy(secondDb, "edge plan", "run-of-its-own");

        StateRepair.Apply(Root, StateRepair.Survey(Root), DateTimeOffset.UtcNow);

        Assert.Equal("run-of-its-own", Assert.Single(RunIdsAt(secondDb)));
    }
}
