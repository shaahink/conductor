using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K3.1 — state has a machine-level home, one catalogue keyed by repo plus plan, an explicit
/// resolution order, and a migration that IMPORTS a pre-K3.1 <c>.conductor/run.db</c> instead of
/// orphaning it.
///
/// <para>Every test drives its own root through the <c>root</c> parameter or a scoped environment
/// variable and never touches the operator's real home — see <see cref="TestEnvironmentIsolation"/>,
/// which pins the whole process to a temp home anyway.</para>
/// </summary>
public sealed class K3_1StateHomeTests : IDisposable
{
    private readonly string _tmp;

    public K3_1StateHomeTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-k31-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string NewRepo(string name)
    {
        var p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.Combine(p, StateHome.ScratchDirName));
        return p;
    }

    private string Root => Path.Combine(_tmp, "home");

    // ── the home itself ────────────────────────────────────────────────────────────────────────

    /// <summary>The default root is the OS's per-machine data location, NOT the repo. This is the
    /// whole point of the checkpoint: before K3.1 the answer was one hard-coded
    /// <c>Path.Combine(Repo, ".conductor")</c>.</summary>
    [Fact]
    public void DefaultRoot_IsMachineLevel_NotRepoLevel()
    {
        var root = StateHome.DefaultRoot;
        Assert.EndsWith("conductor", root, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(local, "conductor"), root);
        }
        else
        {
            Assert.Contains("share", root, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(StateHome.ScratchDirName, root, StringComparison.Ordinal);
    }

    /// <summary>CONDUCTOR_STATE_HOME wins over the OS default — the escape hatch every rig and every
    /// test needs.</summary>
    [Fact]
    public void StateHomeEnvVar_OverridesDefaultRoot()
    {
        var previous = Environment.GetEnvironmentVariable(StateHome.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(StateHome.HomeEnvVar, Root);
            Assert.Equal(Path.GetFullPath(Root), StateHome.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StateHome.HomeEnvVar, previous);
        }
    }

    // ── keying ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Same repo + same plan resolves to the same directory every time — history must not
    /// fork across invocations.</summary>
    [Fact]
    public void Slug_IsStable_ForTheSameRepoAndPlan()
    {
        var repo = NewRepo("alpha");
        Assert.Equal(StateHome.SlugFor(repo, "core"), StateHome.SlugFor(repo, "core"));
    }

    /// <summary>Two plans in one repo get two stores. The catalogue is keyed by repo path PLUS plan,
    /// not by repo alone.</summary>
    [Fact]
    public void Slug_DiffersPerPlan_WithinOneRepo()
    {
        var repo = NewRepo("beta");
        Assert.NotEqual(StateHome.SlugFor(repo, "core"), StateHome.SlugFor(repo, "face"));
    }

    /// <summary>Two repos whose leaf name is identical must not collide — the eight hex digits are
    /// over the full normalised path, not the leaf.</summary>
    [Fact]
    public void Slug_DoesNotCollide_ForSameLeafNameInDifferentParents()
    {
        var a = NewRepo(Path.Combine("one", "conductor"));
        var b = NewRepo(Path.Combine("two", "conductor"));
        Assert.NotEqual(StateHome.SlugFor(a, "core"), StateHome.SlugFor(b, "core"));
        Assert.StartsWith("conductor-core-", StateHome.SlugFor(a, "core"), StringComparison.Ordinal);
    }

    /// <summary>On Windows, <c>C:\Code\x</c> and <c>C:\code\x</c> are ONE directory. Two spellings of
    /// one repo must not produce two histories — the exact shape of bug this project already hit when
    /// the orchestrator env spelled the repo with a capital C.</summary>
    [Fact]
    public void Key_IsCaseFolded_OnWindows()
    {
        var repo = NewRepo("Cased");
        var lower = repo.ToLowerInvariant();
        if (OperatingSystem.IsWindows())
            Assert.Equal(StateHome.KeyFor(repo, "core"), StateHome.KeyFor(lower, "core"));
        else
            Assert.NotEqual(StateHome.KeyFor(repo, "core"), StateHome.KeyFor(lower, "core"));
    }

    /// <summary>A trailing separator is not a different repo.</summary>
    [Fact]
    public void Key_IgnoresTrailingSeparator()
    {
        var repo = NewRepo("trail");
        Assert.Equal(StateHome.KeyFor(repo, "core"),
                     StateHome.KeyFor(repo + Path.DirectorySeparatorChar, "core"));
    }

    // ── resolution precedence ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Derived_LandsUnderTheRoot_NotUnderTheRepo()
    {
        var repo = NewRepo("derived");
        var r = StateHome.Resolve(repo, "core", Root);

        Assert.Equal(StateSource.Derived, r.Source);
        Assert.StartsWith(Path.GetFullPath(Root), Path.GetFullPath(r.RunDbPath), StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(repo), Path.GetFullPath(r.RunDbPath), StringComparison.Ordinal);
        Assert.EndsWith(StateHome.RunDbFileName, r.RunDbPath, StringComparison.Ordinal);
    }

    /// <summary>The repo-local pointer beats derivation. This is the lanes seam: a second working
    /// tree has its own repo path, so it derives its own slug — the pointer is how it reads and
    /// writes the SAME run as the primary tree.</summary>
    [Fact]
    public void Resolve_Pointer_BeatsDerivation_SoASecondWorktreeSharesOneRun()
    {
        var primary = NewRepo("primary");
        var worktree = NewRepo("worktree");
        var shared = StateHome.Resolve(primary, "core", Root).RunDbPath;

        Assert.True(StatePointer.TryWrite(StateHome.PointerPathFor(worktree), shared, "core"));

        var r = StateHome.Resolve(worktree, "core", Root);
        Assert.Equal(StateSource.Pointer, r.Source);
        Assert.Equal(Path.GetFullPath(shared), Path.GetFullPath(r.RunDbPath));
    }

    /// <summary>CONDUCTOR_RUN_DB is the bluntest override and outranks even the pointer.</summary>
    [Fact]
    public void Resolve_RunDbEnvVar_BeatsPointerAndDerivation()
    {
        var repo = NewRepo("envwins");
        var pointed = Path.Combine(_tmp, "pointed.db");
        var forced = Path.Combine(_tmp, "forced.db");
        StatePointer.TryWrite(StateHome.PointerPathFor(repo), pointed, "core");

        var previous = Environment.GetEnvironmentVariable(StateHome.RunDbEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(StateHome.RunDbEnvVar, forced);
            var r = StateHome.Resolve(repo, "core", Root);
            Assert.Equal(StateSource.EnvOverride, r.Source);
            Assert.Equal(Path.GetFullPath(forced), r.RunDbPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StateHome.RunDbEnvVar, previous);
        }
    }

    /// <summary>A corrupt pointer degrades to the derived path instead of taking the CLI down.</summary>
    [Fact]
    public void Resolve_CorruptPointer_FallsBackToDerived()
    {
        var repo = NewRepo("corrupt");
        File.WriteAllText(StateHome.PointerPathFor(repo), "{ this is not json");

        var r = StateHome.Resolve(repo, "core", Root);
        Assert.Equal(StateSource.Derived, r.Source);
    }

    // ── the migration ──────────────────────────────────────────────────────────────────────────

    /// <summary>The headline: a real pre-K3.1 database with real rows is IMPORTED, is readable
    /// through the store at its new home, and the original is still on disk.</summary>
    [Fact]
    public void Resolve_ImportsLegacyRunDb_AndTheRowsSurvive()
    {
        var repo = NewRepo("legacy");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-legacy-1");

        var r = StateHome.Resolve(repo, "core", Root);

        Assert.NotNull(r.Import);
        Assert.Equal(Path.GetFullPath(legacy), r.Import!.From);
        Assert.Equal(Path.GetFullPath(r.RunDbPath), r.Import.To);
        Assert.True(File.Exists(r.RunDbPath), "the imported database exists at the machine home");
        Assert.True(File.Exists(legacy), "the ORIGINAL is left in place - the import copies, it does not move");

        using var imported = new SqliteRunStore(r.RunDbPath, NullLogger<SqliteRunStore>.Instance);
        var sessions = imported.QuerySessions("run-legacy-1");
        Assert.Single(sessions);
        Assert.Equal(7, sessions[0].Number);
    }

    /// <summary>Idempotent, and idempotent in the way that matters: a row written AFTER the import
    /// is not clobbered by a later resolution re-copying the stale legacy file over it.</summary>
    [Fact]
    public void Resolve_IsIdempotent_AndNeverOverwritesPostImportWork()
    {
        var repo = NewRepo("idem");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-idem-1");

        var first = StateHome.Resolve(repo, "core", Root);
        Assert.NotNull(first.Import);

        // Work lands at the new home AFTER the import.
        using (var store = new SqliteRunStore(first.RunDbPath, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId("run-idem-1");
            store.RecordSession("run-idem-1", "K3", 99, "session",
                DateTime.UtcNow, null, null, null, 0, 1, null, null, 0, null);
        }

        var second = StateHome.Resolve(repo, "core", Root);
        Assert.Null(second.Import);                       // nothing was imported the second time
        Assert.Equal(first.RunDbPath, second.RunDbPath);  // and it is the same file

        using var reread = new SqliteRunStore(second.RunDbPath, NullLogger<SqliteRunStore>.Instance);
        Assert.Contains(reread.QuerySessions("run-idem-1"), s => s.Number == 99);
    }

    /// <summary>"Says what it moved" — durably, not just on a stderr line that scrolled away.</summary>
    [Fact]
    public void Import_WritesAReceiptNamingBothPaths()
    {
        var repo = NewRepo("receipt");
        var legacy = StateHome.LegacyDbPathFor(repo);
        SeedLegacy(legacy, "run-receipt-1");

        var r = StateHome.Resolve(repo, "core", Root);
        var receipt = StateMigration.ReadReceipt(r.RunDbPath);

        Assert.NotNull(receipt);
        Assert.Equal(Path.GetFullPath(legacy), receipt!.From);
        Assert.Equal(Path.GetFullPath(r.RunDbPath), receipt.To);
        Assert.True(receipt.Bytes > 0);
        Assert.Contains(StateHome.RunDbFileName, receipt.Files);
        Assert.True(File.Exists(StateMigration.ReceiptPathFor(r.RunDbPath)));
    }

    /// <summary>The un-checkpointed tail of a crashed database lives in the <c>-wal</c> sidecar.
    /// Copying the main file alone would silently drop it.</summary>
    [Fact]
    public void Import_CopiesWalAndShmSidecars()
    {
        var repo = NewRepo("sidecars");
        var legacy = StateHome.LegacyDbPathFor(repo);
        File.WriteAllText(legacy, "main");
        File.WriteAllText(legacy + "-wal", "wal");
        File.WriteAllText(legacy + "-shm", "shm");

        var target = Path.Combine(Root, "sidecars", StateHome.RunDbFileName);
        var import = StateMigration.ImportLegacy(legacy, target);

        Assert.NotNull(import);
        Assert.True(File.Exists(target + "-wal"));
        Assert.True(File.Exists(target + "-shm"));
        Assert.Equal(3, import!.Files.Count);
    }

    /// <summary>Nothing to import is the ordinary case and must be silent, not an error.</summary>
    [Fact]
    public void Import_IsNull_WhenThereIsNoLegacyDatabase()
    {
        var repo = NewRepo("fresh");
        var r = StateHome.Resolve(repo, "core", Root);
        Assert.Null(r.Import);
        Assert.False(File.Exists(r.RunDbPath));
    }

    // ── the catalogue ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalogue_RecordsRepoPlanAndDatabase()
    {
        var repo = NewRepo("cat");
        var r = StateHome.Resolve(repo, "core", Root);

        var entry = StateCatalogue.Find(Root, repo, "core");
        Assert.NotNull(entry);
        Assert.Equal(Path.GetFullPath(repo), entry!.Repo);
        Assert.Equal("core", entry.Plan);
        Assert.Equal(Path.GetFullPath(r.RunDbPath), entry.RunDb);
        Assert.True(entry.FirstSeenUtc > DateTimeOffset.MinValue);
    }

    [Fact]
    public void Catalogue_KeepsTwoPlansOfOneRepoApart()
    {
        var repo = NewRepo("twoplans");
        StateHome.Resolve(repo, "core", Root);
        StateHome.Resolve(repo, "face", Root);

        var entries = StateCatalogue.Read(Root);
        Assert.Equal(2, entries.Count(e => string.Equals(e.Repo, Path.GetFullPath(repo), StringComparison.Ordinal)));
        Assert.NotEqual(
            entries.First(e => e.Plan == "core").RunDb,
            entries.First(e => e.Plan == "face").RunDb);
    }

    [Fact]
    public void Catalogue_UpsertsRatherThanDuplicating()
    {
        var repo = NewRepo("upsert");
        StateHome.Resolve(repo, "core", Root);
        var first = StateCatalogue.Find(Root, repo, "core")!;
        StateHome.Resolve(repo, "core", Root);
        var second = StateCatalogue.Find(Root, repo, "core")!;

        Assert.Single(StateCatalogue.Read(Root), e => e.Key == StateHome.KeyFor(repo, "core"));
        Assert.Equal(first.FirstSeenUtc, second.FirstSeenUtc);
        Assert.True(second.LastSeenUtc >= first.LastSeenUtc);
    }

    [Fact]
    public void Catalogue_RecordsWhereAnImportedDatabaseCameFrom()
    {
        var repo = NewRepo("catimport");
        SeedLegacy(StateHome.LegacyDbPathFor(repo), "run-cat-1");

        StateHome.Resolve(repo, "core", Root);
        var entry = StateCatalogue.Find(Root, repo, "core");

        Assert.NotNull(entry);
        Assert.Equal(Path.GetFullPath(StateHome.LegacyDbPathFor(repo)), entry!.ImportedFrom);
        Assert.NotNull(entry.ImportedAtUtc);
    }

    /// <summary>A corrupt index costs a rebuild, never a run — the databases are the truth.</summary>
    [Fact]
    public void Catalogue_SurvivesACorruptFile()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(StateHome.CataloguePathFor(Root), "not json at all");
        var repo = NewRepo("corruptcat");

        Assert.Empty(StateCatalogue.Read(Root));
        StateHome.Resolve(repo, "core", Root);
        Assert.NotNull(StateCatalogue.Find(Root, repo, "core"));
    }

    // ── the wiring ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The property every caller now uses. It must NOT be under the repo, and
    /// <see cref="PlanConfig.StateDir"/> must still be, because the scratch, the discovery files and
    /// the tracked deliverables stay in the working tree.</summary>
    [Fact]
    public void PlanConfig_RunDbPath_LeavesTheRepo_WhileStateDirStays()
    {
        var repo = NewRepo("wiring");
        var previous = Environment.GetEnvironmentVariable(StateHome.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(StateHome.HomeEnvVar, Root);
            var plan = new PlanConfig { Name = "core", Repo = repo };

            Assert.Equal(Path.Combine(repo, StateHome.ScratchDirName), plan.StateDir);
            Assert.StartsWith(Path.GetFullPath(Root), Path.GetFullPath(plan.RunDbPath), StringComparison.Ordinal);
            Assert.NotEqual(Path.Combine(plan.StateDir, StateHome.RunDbFileName), plan.RunDbPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StateHome.HomeEnvVar, previous);
        }
    }

    /// <summary>Resolution is cached per loaded plan: repeated access must not re-run the migration
    /// or re-hit the catalogue on every command.</summary>
    [Fact]
    public void PlanConfig_ResolveState_IsCachedPerPlan()
    {
        var repo = NewRepo("cached");
        var previous = Environment.GetEnvironmentVariable(StateHome.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(StateHome.HomeEnvVar, Root);
            var plan = new PlanConfig { Name = "core", Repo = repo };
            Assert.Same(plan.ResolveState(), plan.ResolveState());
        }
        finally
        {
            Environment.SetEnvironmentVariable(StateHome.HomeEnvVar, previous);
        }
    }

    /// <summary>Writes a pre-K3.1 database with a real schema and a real session row, so the import
    /// is proven on a database the store can actually read back — not on a placeholder file.</summary>
    private static void SeedLegacy(string path, string runId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new SqliteRunStore(path, NullLogger<SqliteRunStore>.Instance);
        store.SetRunId(runId);
        store.InitializeRun(runId, "core", Path.GetDirectoryName(path)!, "main", Conductor.Core.EngineStamp.Parse("test"));
        store.InitializeStage(runId, "K1", "Stage One");
        store.RecordSession(runId, "K1", 7, "session",
            DateTime.UtcNow, null, null, null, 0, 1, null, null, 0, null);
    }
}
