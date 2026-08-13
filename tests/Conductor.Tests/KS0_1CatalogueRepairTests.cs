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

    /// <summary>
    /// A home is repaired within its own walls. A catalogue is a list of ABSOLUTE paths and nothing
    /// stops one pointing outside the home that holds it — copy a state home and its index still
    /// names the original machine's stores, so a rehearsal "on a copy" plans deletions against the
    /// live home. Measured on 2026-08-13 doing exactly that, which is why this test exists.
    /// </summary>
    [Fact]
    public void AStoreOutsideTheHomeIsNeverWritten()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        var insideDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;

        // A second copy of the same history, catalogued into this home but living somewhere else -
        // the shape a copied state home has, seen from the copy.
        var elsewhere = Path.Combine(_tmp, "another-home", "runs", "far-away", StateHome.RunDbFileName);
        Assert.NotNull(StateMigration.ImportLegacy(StateHome.LegacyDbPathFor(repo), elsewhere));
        StateCatalogue.Upsert(Root, repo, "far plan", elsewhere, null);

        var plan = StateRepair.Survey(Root);
        var dup = Assert.Single(plan.Duplicates);

        Assert.DoesNotContain(dup.RemoveFrom, p => StateMigration.PathsEqual(p, elsewhere));
        Assert.Contains(plan.Deferred, d => d.Contains("outside this state home", StringComparison.Ordinal));

        StateRepair.Apply(Root, plan, DateTimeOffset.UtcNow);
        Assert.Equal("run-alpha", Assert.Single(RunIdsAt(elsewhere)));
        Assert.Equal("run-alpha", Assert.Single(RunIdsAt(insideDb)));

        // and it refuses even when handed a plan that names it outright
        var forged = plan with
        {
            Duplicates = [dup with { RemoveFrom = [Path.GetFullPath(elsewhere)] }],
        };
        Assert.Throws<InvalidOperationException>(() => StateRepair.Apply(Root, forged, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The rule the first version of this pass did not have, and the one it cost a confirmed
    /// checkpoint to learn: copies of one legacy database are NOT interchangeable. K3.1 moved run.db
    /// to the state home, so a run kept writing into its OWN slug store while the legacy path froze,
    /// and every later import of that frozen file holds a TRUNCATED copy. The keeper must be the copy
    /// that CONTAINS the others - even when that means keeping the copy in the store that is not live.
    /// </summary>
    [Fact]
    public void TheFullerCopyIsKept_EvenWhenTheStaleOneIsLive()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        var fullDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        var staleDb = ImportAgainTheOldWay(repo, "edge plan");

        // The run went on writing into its own store after the copy was taken.
        using (var store = new SqliteRunStore(fullDb, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId("run-alpha");
            store.RecordSession("run-alpha", "K1", 8, "session",
                DateTime.UtcNow, null, null, null, 0, 1, null, null, 0, null);
        }

        // ...and the TRUNCATED copy is the one a live engine is using - so the safety rule and the
        // ownership rule now disagree, which is exactly the case that went wrong for real.
        var me = System.Diagnostics.Process.GetCurrentProcess();
        Exec(staleDb, "UPDATE runs SET status = 'running' WHERE run_id = 'run-alpha'");
        using (var c = new SqliteConnection($"Data Source={staleDb}"))
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

        Assert.Equal(Path.GetFullPath(fullDb), dup.OwnerDb);
        Assert.Contains("fullest copy", dup.OwnerReason, StringComparison.Ordinal);
        Assert.Empty(dup.RemoveFrom);          // the only other copy is live, so nothing can be removed
        Assert.Contains(plan.Deferred, d => d.Contains("live engine is using", StringComparison.Ordinal));

        StateRepair.Apply(Root, plan, DateTimeOffset.UtcNow);

        // nothing lost: the fuller copy still has both sessions
        using var check = new SqliteRunStore(fullDb, NullLogger<SqliteRunStore>.Instance);
        Assert.Equal(2, check.QuerySessions("run-alpha").Count);
    }

    /// <summary>
    /// Between sessions there is no live agent pid, but the engine is still driving the run — the
    /// pids table tracks agents and faces, never the engine itself. A liveness test that only asked
    /// the pids table would call that store idle and write it out from under a running engine.
    /// </summary>
    [Fact]
    public void AnEngineHoldingTheRepoCountsAsLive_EvenWithNoAgentPid()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        var firstDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        var secondDb = ImportAgainTheOldWay(repo, "edge plan");

        // no pid rows at all - just a run that says running and an engine lock held by a live process.
        // The other copy is a finished run, so the engine lock alone does not make IT live.
        Exec(firstDb, "UPDATE runs SET status = 'Completed' WHERE run_id = 'run-alpha'");
        Exec(secondDb, "UPDATE runs SET status = 'running' WHERE run_id = 'run-alpha'");
        var me = System.Diagnostics.Process.GetCurrentProcess();
        var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, Conductor.Core.EngineLock.FileName),
            me.Id + Environment.NewLine + me.StartTime.ToUniversalTime().ToString("o"));

        var store = Assert.Single(StateRepair.Survey(Root).Stores,
            s => StateMigration.PathsEqual(s.Db, secondDb));
        Assert.True(store.Live, "an engine holding the repo's lock is using that store");

        // and so it is never written: both copies are complete, so the live one keeps them.
        var dup = Assert.Single(StateRepair.Survey(Root).Duplicates);
        Assert.DoesNotContain(dup.RemoveFrom, p => StateMigration.PathsEqual(p, secondDb));
        Assert.Equal(Path.GetFullPath(secondDb), dup.OwnerDb);
        Assert.Equal(Path.GetFullPath(firstDb), Assert.Single(dup.RemoveFrom));
    }

    /// <summary>Copies that have each gained something the other lacks are not deduplicated at all.
    /// There is no lossless choice there, so the pass says so and stops.</summary>
    [Fact]
    public void DivergedCopiesAreLeftForAPerson()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "core plan", "run-alpha");
        var oneDb = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        var twoDb = ImportAgainTheOldWay(repo, "edge plan");

        foreach (var (db, n) in new[] { (oneDb, 8), (twoDb, 9) })
        {
            using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
            store.SetRunId("run-alpha");
            store.RecordSession("run-alpha", "K1", n, "session",
                DateTime.UtcNow, null, null, null, 0, 1, null, null, 0, null);
        }

        var plan = StateRepair.Survey(Root);

        Assert.Empty(plan.Duplicates);
        Assert.Contains(plan.Deferred, d => d.Contains("DIVERGED", StringComparison.Ordinal));

        var outcome = StateRepair.Apply(Root, plan, DateTimeOffset.UtcNow);
        Assert.Equal(0, outcome.RowsDeleted);
        Assert.Equal(2, StateDedup.Stores(Root).Sum(s => RunIdsAt(s).Count));
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
