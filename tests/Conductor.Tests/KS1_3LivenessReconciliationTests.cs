using System.Security.Cryptography;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Fleet;
using Conductor.Core.History;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS1.3 — a listing may not repeat a claim nobody has checked, and it may not invent a run to hang a
/// broken catalogue entry on.
///
/// <para>Two defects, one checkpoint. The first: <c>runs.status</c> is what the last engine to write
/// the row believed, and an engine that is killed never writes the correction — four rows on this
/// machine still say <c>running</c> for engines that died in July. Every reading surface repeated it.
/// The second: every catalogue entry the archive could not open was emitted into <c>runs[]</c> as a
/// run with an EMPTY id, and six of those collided on that key in a downstream harvest, which then
/// refused the whole payload.</para>
///
/// <para>The rule that settles the first is the repair pass's rule, called rather than re-implemented
/// (<see cref="RunLiveness"/>), and it is applied at RENDER time only: these tests hash the database
/// and the catalogue either side of a listing and require the bytes to be identical, because the
/// moment reconciliation writes anything it has become a repair, and repair is a verb an operator
/// runs on purpose.</para>
/// </summary>
public sealed class KS1_3LivenessReconciliationTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    public KS1_3LivenessReconciliationTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks13-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ fixtures

    private string RepoPath(string name)
    {
        var p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Writes a run through the real writer and catalogues it, exactly as the engine would.
    /// <paramref name="trackLivePid"/> records THIS test process as a tracked child, which is the only
    /// honest way to seed "a live pid" without spawning something to kill.</summary>
    private string SeedRun(
        string repo, string plan, string runId, string status = "running", bool trackLivePid = false)
    {
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", EngineStamp.Parse("0.3.1-alpha+test"));
            store.SetRunId(runId);
            store.InitializeStage(runId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });
            store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "work", Attempt = 1 });
            store.RecordSession(runId, "S1", 1, "work",
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 1, 0, 30, 0, DateTimeKind.Utc), "advance",
                agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: "ok", resultSummary: "one", commitCount: 1, newlyDone: null);
            store.SeedCheckpoints(runId, [("C1", "S1", "First checkpoint", "DONE", "abc1234", "e.md")]);
            if (trackLivePid)
            {
                using var me = System.Diagnostics.Process.GetCurrentProcess();
                store.TrackPid(Environment.ProcessId, runId, "agent", "S1", 1, me.StartTime.ToUniversalTime());
            }
            if (!string.Equals(status, "running", StringComparison.Ordinal)) store.RecordRunEnd(runId, status);
        }
        StateCatalogue.Upsert(_root, repo, plan, db);
        SqliteConnection.ClearAllPools();
        return db;
    }

    private static RunHistoryRow Single(IReadOnlyList<RunHistoryRow> rows) => Assert.Single(rows);

    // ------------------------------------------------------------------ the reconciled word

    [Fact]
    public void KilledEngineNeverListsAsRunning()
    {
        var repo = RepoPath("killed");
        SeedRun(repo, "core", "run-killed-0001");

        var row = Single(RunHistory.List(_root));

        // What the column holds is preserved; what the listing SAYS is the checked answer.
        Assert.Equal("running", row.StoredStatus);
        Assert.Equal(RunLiveness.Orphaned, row.Status);
        Assert.False(row.StoreLooksLive);

        var item = RunHistoryPayload.Item(row);
        Assert.Equal(RunLiveness.Orphaned, item.Status);
        Assert.Equal("running", item.StoredStatus);
        Assert.False(item.StoreLive);
    }

    [Fact]
    public void LivePidStillListsAsRunning()
    {
        var repo = RepoPath("livepid");
        SeedRun(repo, "core", "run-livepid-01", trackLivePid: true);

        var row = Single(RunHistory.List(_root));

        Assert.True(row.StoreLooksLive);
        Assert.Equal("running", row.Status);
    }

    [Fact]
    public void EngineLockHeldStillListsAsRunning()
    {
        // The pids table tracks agents, not the engine, so BETWEEN sessions a healthy run has no live
        // pid at all. The lock is what answers for that gap, and a reconciler that ignored it would
        // label a run mid-gate-battery orphaned.
        var repo = RepoPath("locked");
        SeedRun(repo, "core", "run-locked-0001");
        var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
        Directory.CreateDirectory(stateDir);
        EngineLock.Write(stateDir);          // this process, with its real start time

        var row = Single(RunHistory.List(_root));

        Assert.True(row.StoreLooksLive);
        Assert.Equal("running", row.Status);

        EngineLock.Delete(stateDir);
        Assert.Equal(RunLiveness.Orphaned, Single(RunHistory.List(_root)).Status);
    }

    [Fact]
    public void AFinishedRunIsNeverReconciled()
    {
        // Terminal is terminal whoever is or is not holding the file — reconciliation may only ever
        // contradict a claim that something is still HAPPENING.
        var repo = RepoPath("finished");
        SeedRun(repo, "core", "run-finished-01", status: "completed");

        var row = Single(RunHistory.List(_root));

        Assert.Equal("completed", row.Status);
        Assert.Equal("completed", row.StoredStatus);
    }

    [Fact]
    public void TheReconciledWordFitsTheColumnsThatMustPrintIt()
    {
        // `conductor history`'s STATUS cell is eight wide and the Face picker is no wider. A word that
        // does not fit is clipped to an ellipsis, and a reconciled status nobody can read is no fix.
        Assert.True(RunLiveness.Orphaned.Length <= 8,
            $"'{RunLiveness.Orphaned}' is {RunLiveness.Orphaned.Length} characters; the STATUS column is 8.");
    }

    [Fact]
    public void TheFacePickerCarriesTheReconciledStatus()
    {
        var repo = RepoPath("picker");
        SeedRun(repo, "core", "run-picker-0001");

        var past = Assert.Single(FacePastRuns.Read(_root).Rows);

        Assert.Equal("run-picker-0001", past.RunId);
        Assert.Equal(RunLiveness.Orphaned, past.Status);
    }

    // ------------------------------------------------------------------ render-time only

    [Fact]
    public void ListingWritesNothing()
    {
        var repo = RepoPath("readonly");
        var db = SeedRun(repo, "core", "run-readonly-1");
        var cataloguePath = StateHome.CataloguePathFor(_root);

        var dbBefore = Sha256(db);
        var catalogueBefore = Sha256(cataloguePath);

        var rows = RunHistory.List(_root);
        _ = RunHistoryPayload.List(rows);
        _ = FacePastRuns.Read(_root);
        RunHistory.Find(_root, "run-readonly-1", out _);
        SqliteConnection.ClearAllPools();

        Assert.Equal(RunLiveness.Orphaned, Single(rows).Status);   // it did reconcile
        Assert.Equal(dbBefore, Sha256(db));                         // and it wrote nothing
        Assert.Equal(catalogueBefore, Sha256(cataloguePath));
        // The two files the clause names are the two files hashed. The -wal/-shm sidecars SQLite puts
        // beside a WAL database when ANY connection opens it — including a Mode=ReadOnly one — are
        // SQLite's own bookkeeping and predate this checkpoint by the whole of K3.2; asserting on them
        // would be pinning the database engine, not this code.

        // And the door itself is still bolted: the archive a listing opens is Mode=ReadOnly, so the
        // guarantee is SQLite's rather than this test's vigilance.
        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);
        Assert.Throws<SqliteException>(() => archive!.Query("UPDATE runs SET status = 'completed'"));
        SqliteConnection.ClearAllPools();
        Assert.Equal(dbBefore, Sha256(db));
    }

    // ------------------------------------------------------------------ the payload

    /// <summary>Three catalogue entries, one of each kind the index can hold.</summary>
    private (string Good, string Vanished, string NotADb) SeedThreeEntries()
    {
        var good = RepoPath("good");
        SeedRun(good, "core", "run-good-000001", status: "completed");

        var vanished = RepoPath("vanished");
        StateCatalogue.Upsert(_root, vanished, "core",
            Path.Combine(_root, "runs", StateHome.SlugFor(vanished, "core"), StateHome.RunDbFileName));

        var notADb = RepoPath("notadb");
        var junk = Path.Combine(_tmp, "not-a-run.db");
        File.WriteAllText(junk, "this file exists and it is not a run database");
        StateCatalogue.Upsert(_root, notADb, "core", junk);

        return (good, vanished, junk);
    }

    [Fact]
    public void JsonNeverEmitsBlankRunId()
    {
        SeedThreeEntries();

        var payload = RunHistoryPayload.List(RunHistory.List(_root));

        Assert.Single(payload.Runs);
        Assert.All(payload.Runs, r => Assert.False(string.IsNullOrEmpty(r.RunId)));
        Assert.Equal(2, payload.Unreadable!.Count);

        // And through the serialiser the command actually uses, because the contract is the bytes.
        var json = JsonSerializer.Serialize(payload, RunHistoryJsonContext.Default.RunHistoryListJson);
        using var doc = JsonDocument.Parse(json);
        var runs = doc.RootElement.GetProperty("runs");
        Assert.Equal(1, runs.GetArrayLength());
        foreach (var r in runs.EnumerateArray())
            Assert.False(string.IsNullOrEmpty(r.GetProperty("runId").GetString()));
        Assert.Equal(2, doc.RootElement.GetProperty("unreadable").GetArrayLength());
    }

    [Fact]
    public void MissingDbAndNonRunDbAreDistinguished()
    {
        var (_, vanished, junk) = SeedThreeEntries();

        var payload = RunHistoryPayload.List(RunHistory.List(_root));
        var reasons = payload.Unreadable!.ToDictionary(u => u.RunDb, u => u.Reason, StringComparer.OrdinalIgnoreCase);

        var vanishedDb = Path.Combine(_root, "runs", StateHome.SlugFor(vanished, "core"), StateHome.RunDbFileName);
        Assert.Equal(RunHistoryPayload.ReasonMissing, reasons[vanishedDb]);
        Assert.Equal(RunHistoryPayload.ReasonNotARunDatabase, reasons[junk]);
        Assert.NotEqual(RunHistoryPayload.ReasonMissing, RunHistoryPayload.ReasonNotARunDatabase);
    }

    [Fact]
    public void ShapingAnUnreadableRowAsARunIsRefusedRatherThanBlanked()
    {
        // The defect was a silent fallback that made up an empty id. If a future caller reaches for
        // Item() with a row that has no run, it must stop rather than mint another blank one.
        var (_, vanished, _) = SeedThreeEntries();
        var row = RunHistory.List(_root).First(r =>
            !r.Readable && r.RunDbPath.Contains(StateHome.SlugFor(vanished, "core"), StringComparison.OrdinalIgnoreCase));

        Assert.Throws<ArgumentException>(() => RunHistoryPayload.Item(row));
    }

    [Fact]
    public void ReconciliationIsNotRepair()
    {
        // The catalogue keeps every entry it had, including the two that do not open: listing them
        // honestly is this checkpoint's job, and removing them is `conductor state repair`'s.
        SeedThreeEntries();
        var before = StateCatalogue.Read(_root).Count;

        _ = RunHistoryPayload.List(RunHistory.List(_root));

        Assert.Equal(3, before);
        Assert.Equal(before, StateCatalogue.Read(_root).Count);
    }

    // ------------------------------------------------------------------ one rule, not two

    [Fact]
    public void TheRepairPassAndTheListingAgree()
    {
        // Not asserted from source: the repair pass's survey and the listing are asked about the same
        // store and must return the same liveness. A second implementation would drift from this the
        // first time either changed.
        var repo = RepoPath("agreement");
        SeedRun(repo, "core", "run-agree-00001");

        var listing = Single(RunHistory.List(_root)).StoreLooksLive;
        var survey = StateRepair.Survey(_root).Stores.Single(s => s.Runs.Any(r => r.RunId == "run-agree-00001"));

        Assert.Equal(survey.Live, listing);
        Assert.False(listing);

        var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
        Directory.CreateDirectory(stateDir);
        EngineLock.Write(stateDir);
        try
        {
            var listingLive = Single(RunHistory.List(_root)).StoreLooksLive;
            var surveyLive = StateRepair.Survey(_root).Stores.Single(s => s.Runs.Any(r => r.RunId == "run-agree-00001"));
            Assert.Equal(surveyLive.Live, listingLive);
            Assert.True(listingLive);
        }
        finally
        {
            EngineLock.Delete(stateDir);
        }
    }

    private static string Sha256(string path)
    {
        if (!File.Exists(path)) return "absent";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
