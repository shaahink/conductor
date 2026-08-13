using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS0.1 — the import stops keying on the plan slug and starts keying on the run id.
///
/// <para>The defect these pin, measured on the operator's own machine on 2026-08-13: one
/// <c>C:\code\conductor\.conductor\run.db</c> had been imported under FIVE plan slugs, so 25 real
/// runs were 37 rows in <c>conductor history</c> and payesh's harvest refused to run over the
/// collision. Nothing was wrong with the copying — the copy is careful. What was wrong is that
/// "have I imported this already?" was answered by whether the DESTINATION existed, and the
/// destination is derived from the plan name.</para>
///
/// <para>Every test drives its own root and its own temp repo; <see cref="TestEnvironmentIsolation"/>
/// pins the process to a temp home besides.</para>
/// </summary>
[Collection(StateSinkCollection.Name)]
public sealed class KS0_1ImportDedupTests : IDisposable
{
    private readonly string _tmp;

    public KS0_1ImportDedupTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks01-" + Guid.NewGuid().ToString("N")[..10]);
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

    /// <summary>A legacy database with real rows in it, the way K3.1's own tests build one.</summary>
    private static void SeedLegacy(string path, params string[] runIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new SqliteRunStore(path, NullLogger<SqliteRunStore>.Instance);
        foreach (var runId in runIds)
        {
            store.SetRunId(runId);
            store.InitializeRun(runId, "core", Path.GetDirectoryName(path)!, "main",
                Conductor.Core.EngineStamp.Parse("test"));
            store.InitializeStage(runId, "K1", "Stage One");
            store.RecordSession(runId, "K1", 7, "session",
                DateTime.UtcNow, null, null, null, 0, 1, null, null, 0, null);
        }
    }

    private static IReadOnlyList<string> RunIdsAt(string db) => StateDedup.RunIds(db) ?? [];

    // ── the headline ───────────────────────────────────────────────────────────────────────────

    /// <summary>The whole checkpoint in one test: a SECOND plan in the same repo does not import the
    /// same database again, so the machine's history does not grow a second copy of every run.</summary>
    [Fact]
    public void ASecondPlanInTheSameRepo_ImportsNothing()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "run-alpha", "run-beta");

        var first = StateHome.Resolve(repo, "core", Root);
        Assert.NotNull(first.Import);
        Assert.Equal(2, RunIdsAt(first.RunDbPath).Count);

        var second = StateHome.Resolve(repo, "edge", Root);

        Assert.Null(second.Import);
        Assert.NotEqual(first.RunDbPath, second.RunDbPath);
        Assert.False(File.Exists(second.RunDbPath),
            "the second slug must not be handed a copy of the first slug's history");

        // What the falsifiable exit actually says: across every store this machine has, the run ids
        // are still one apiece.
        var everyRow = StateDedup.Stores(Root).SelectMany(RunIdsAt).ToList();
        Assert.Equal(2, everyRow.Count);
        Assert.Equal(everyRow.Count, everyRow.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Three read-only invocations under three different plans — the exact shape that grew
    /// the real catalogue on 2026-08-13 — add zero rows.</summary>
    [Fact]
    public void ThreeMorePlans_AddZeroRows()
    {
        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "run-alpha", "run-beta", "run-gamma");
        StateHome.Resolve(repo, "core", Root);

        var before = StateDedup.Stores(Root).SelectMany(RunIdsAt).Count();
        foreach (var plan in new[] { "audit", "lanes", "probe" }) StateHome.Resolve(repo, plan, Root);
        var after = StateDedup.Stores(Root).SelectMany(RunIdsAt).Count();

        Assert.Equal(3, before);
        Assert.Equal(before, after);
    }

    /// <summary>A skip is never silent. "Your history is somewhere else" is exactly the sentence a
    /// quiet skip costs, and this project has paid it once already (bug #33).</summary>
    [Fact]
    public void TheSkip_SaysWhereTheHistoryWent()
    {
        var warnings = new List<string>();
        StateMigration.Warn = warnings.Add;

        var repo = NewRepo("old-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "run-alpha");
        var first = StateHome.Resolve(repo, "core", Root);
        warnings.Clear();

        StateHome.Resolve(repo, "edge", Root);

        var line = Assert.Single(warnings);
        Assert.Contains("already imported", line, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(first.RunDbPath), line, StringComparison.Ordinal);
    }

    // ── the evidence, in order ─────────────────────────────────────────────────────────────────

    /// <summary>The receipt answers first, and names the store that holds the history.</summary>
    [Fact]
    public void TheReceiptIsTheFirstEvidence()
    {
        var repo = NewRepo("old-repo");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-alpha");
        var first = StateHome.Resolve(repo, "core", Root);

        var target = StateHome.DerivedRunDbPath(Root, repo, "edge");
        var prior = StateDedup.FindPriorImport(Root, legacy, target);

        Assert.NotNull(prior);
        Assert.Equal(PriorImportEvidence.Receipt, prior!.Evidence);
        Assert.Equal(Path.GetFullPath(first.RunDbPath), prior.TargetDb);
        Assert.Empty(prior.MissingRunIds);
    }

    /// <summary>The run ids are the evidence that outlives the paperwork: delete the receipt AND the
    /// catalogue — the two things K3.1 itself calls losable — and the answer is unchanged, because it
    /// is the runs that are duplicated, not the bookkeeping.</summary>
    [Fact]
    public void TheRunIdsAnswerWhenThePaperworkIsGone()
    {
        var repo = NewRepo("old-repo");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-alpha", "run-beta");
        var first = StateHome.Resolve(repo, "core", Root);

        File.Delete(StateMigration.ReceiptPathFor(first.RunDbPath));
        File.Delete(StateHome.CataloguePathFor(Root));

        var prior = StateDedup.FindPriorImport(Root, legacy, StateHome.DerivedRunDbPath(Root, repo, "edge"));

        Assert.NotNull(prior);
        Assert.Equal(PriorImportEvidence.RunIds, prior!.Evidence);
        Assert.Equal(Path.GetFullPath(first.RunDbPath), prior.TargetDb);

        var second = StateHome.Resolve(repo, "edge", Root);
        Assert.Null(second.Import);
        Assert.False(File.Exists(second.RunDbPath));
    }

    /// <summary>A legacy file that has gained a run since the import is the one shape a person has to
    /// look at: copying it would duplicate everything else in it, and skipping leaves the new run
    /// where it is. So the skip stands, and the warning names what is being left behind.</summary>
    [Fact]
    public void RunsThisMachineHasNeverSeen_AreNamedRatherThanCopied()
    {
        var warnings = new List<string>();
        StateMigration.Warn = warnings.Add;

        var repo = NewRepo("old-repo");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-alpha");
        StateHome.Resolve(repo, "core", Root);
        SeedLegacy(legacy, "run-later");     // the old engine kept writing its own database
        warnings.Clear();

        var second = StateHome.Resolve(repo, "edge", Root);

        Assert.Null(second.Import);
        Assert.False(File.Exists(second.RunDbPath));
        var line = Assert.Single(warnings);
        Assert.Contains("run-late", line, StringComparison.Ordinal);
        Assert.Contains("no record of", line, StringComparison.Ordinal);
    }

    // ── what must NOT change ───────────────────────────────────────────────────────────────────

    /// <summary>The first import still happens. A guard that refuses everything would pass every test
    /// above and lose the history K3.1 exists to keep.</summary>
    [Fact]
    public void AFirstImportIsUntouched()
    {
        var repo = NewRepo("fresh-repo");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "run-only");

        var r = StateHome.Resolve(repo, "core", Root);

        Assert.NotNull(r.Import);
        Assert.True(File.Exists(r.RunDbPath));
        Assert.Equal("run-only", Assert.Single(RunIdsAt(r.RunDbPath)));
    }

    /// <summary>A DIFFERENT repo's legacy database is a different file with different runs, and is
    /// imported normally however many stores this machine already has.</summary>
    [Fact]
    public void ADifferentReposLegacyDb_StillImports()
    {
        var one = NewRepo("repo-one");
        SeedLegacy(StateHome.LegacyDbPathFor(one), "run-one");
        StateHome.Resolve(one, "core", Root);

        var two = NewRepo("repo-two");
        SeedLegacy(StateHome.LegacyDbPathFor(two), "run-two");
        var r = StateHome.Resolve(two, "core", Root);

        Assert.NotNull(r.Import);
        Assert.Equal("run-two", Assert.Single(RunIdsAt(r.RunDbPath)));
    }

    /// <summary>Bug #33's refresh still fires for the slug that owns the copy: the source moving on
    /// is not a duplicate, it is the same store gaining rows.</summary>
    [Fact]
    public void TheSameSlugStillRefreshesFromASourceThatMovedOn()
    {
        var repo = NewRepo("old-repo");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-alpha");
        var first = StateHome.Resolve(repo, "core", Root);
        Assert.NotNull(first.Import);

        SeedLegacy(legacy, "run-later");
        var again = StateHome.Resolve(repo, "core", Root);

        Assert.NotNull(again.Import);
        Assert.True(again.Import!.Refreshed);
        Assert.Equal(2, RunIdsAt(again.RunDbPath).Count);
    }

    /// <summary>...but NOT once the repair pass has been over that store. A deduplicated copy is
    /// deliberately a subset of its source; without this guard the next resolution reads that as
    /// "the source is ahead" and copies every removed row straight back in.</summary>
    [Fact]
    public void ARepairedStoreIsNeverRefreshedBackIntoDuplicates()
    {
        var repo = NewRepo("old-repo");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-alpha", "run-beta");
        var first = StateHome.Resolve(repo, "core", Root);

        // Stand in for the repair: run-beta now belongs to another store, so it leaves this one.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={first.RunDbPath}"))
        {
            c.Open();
            StateRepair.DeleteRun(c, null, StateRepair.RunIdTables(c), "run-beta");
        }
        Assert.True(StateMigration.MarkRepaired(first.RunDbPath, DateTimeOffset.UtcNow));

        var again = StateHome.Resolve(repo, "core", Root);

        Assert.Null(again.Import);
        Assert.Equal("run-alpha", Assert.Single(RunIdsAt(again.RunDbPath)));
    }
}
