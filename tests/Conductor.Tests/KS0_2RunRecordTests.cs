using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS0.2 — a run record can be closed, and a parked run stops claiming to be running.
///
/// <para>The defect these reproduce is three eras old (FU-F1-06, filed 2026-07-10): <c>runs.status</c>
/// had exactly two writers, one that sets <c>running</c> at every process start and one that sets a
/// terminal word at completion. A run that stopped any other way — <c>NeedsHuman</c>, <c>Paused</c>,
/// or an engine simply killed — said <c>running</c> for ever. Four rows on the operator's machine say
/// it today, and the only correction anyone had ever managed was hand-edited SQL in two databases.</para>
/// </summary>
[Collection(StateSinkCollection.Name)]
public sealed class KS0_2RunRecordTests : IDisposable
{
    private readonly string _tmp;

    public KS0_2RunRecordTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks02-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Root => Path.Combine(_tmp, "home");

    // ───────────────────────────────────────────────────────── the status-only writer (FU-F1-06)

    /// <summary>The whole of FU-F1-06 in one assertion: a park is written, and no ending is invented
    /// for a run that can still be resumed. <c>RecordRunEnd</c> could not be reused for this precisely
    /// because it stamps <c>ended_utc</c>, which is what kept the row lying for three eras.</summary>
    [Fact]
    public void UpdateRunStatus_writesTheStatusAndInventsNoEnding()
    {
        var db = Path.Combine(_tmp, "s1", "run.db");
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        Seed(store, "run-park");

        store.UpdateRunStatus("run-park", RunRecord.StatusText(Conductor.Models.RunStatus.NeedsHuman));

        var row = Assert.Single(store.Query("SELECT status, ended_utc FROM runs WHERE run_id = 'run-park'"));
        Assert.Equal("needs_human", row["status"]);
        Assert.Null(row["ended_utc"]);
    }

    /// <summary>A run resumed out of a park must not have a real ending erased, and must not keep the
    /// park either. The status-only writer is the only thing that can do both.</summary>
    [Fact]
    public void UpdateRunStatus_leavesAnEndingThatIsAlreadyThere()
    {
        var db = Path.Combine(_tmp, "s2", "run.db");
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        Seed(store, "run-done");
        store.RecordRunEnd("run-done", "Completed");

        store.UpdateRunStatus("run-done", "running");

        var row = Assert.Single(store.Query("SELECT status, ended_utc FROM runs WHERE run_id = 'run-done'"));
        Assert.Equal("running", row["status"]);
        Assert.NotNull(row["ended_utc"]);
    }

    /// <summary>The vocabulary is one vocabulary. Only the states that survive the engine's exit get
    /// their own word: an engine in <c>Idle</c>, <c>Waiting</c>, <c>Backoff</c> or
    /// <c>VerifyingGates</c> is alive and about to spawn a session, and a row that stopped saying
    /// <c>running</c> under a live engine is a row the repair pass would believe it may write.</summary>
    [Theory]
    [InlineData(Conductor.Models.RunStatus.Running, "running")]
    [InlineData(Conductor.Models.RunStatus.Idle, "running")]
    [InlineData(Conductor.Models.RunStatus.Waiting, "running")]
    [InlineData(Conductor.Models.RunStatus.Backoff, "running")]
    [InlineData(Conductor.Models.RunStatus.VerifyingGates, "running")]
    [InlineData(Conductor.Models.RunStatus.Paused, "paused")]
    [InlineData(Conductor.Models.RunStatus.NeedsHuman, "needs_human")]
    [InlineData(Conductor.Models.RunStatus.AwaitingOwner, "awaiting_owner")]
    public void OnlyTheParksThatOutliveTheEngineGetTheirOwnWord(Conductor.Models.RunStatus status, string expected)
        => Assert.Equal(expected, RunRecord.StatusText(status));

    /// <summary>Rows written before this checkpoint carry <c>Completed</c> and <c>Aborted</c> in
    /// PascalCase and mean it. Anything else — a park, a blank, a word from a future era — counts as
    /// unfinished, because the cost of guessing wrong is writing a store an engine is using.</summary>
    [Theory]
    [InlineData("Completed", true)]
    [InlineData("completed", true)]
    [InlineData("Aborted", true)]
    [InlineData("closed", true)]
    [InlineData("running", false)]
    [InlineData("needs_human", false)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TerminalIsAShortListAndEverythingElseIsUnfinished(string? status, bool terminal)
        => Assert.Equal(terminal, RunRecord.IsTerminal(status));

    /// <summary>
    /// The interaction that makes the widening in <see cref="StateRepair"/> load-bearing rather than
    /// tidy. Once parks are written, a run an engine is holding open at a <c>needs_human</c> prompt no
    /// longer says <c>running</c> — and the old <c>status = 'running'</c> liveness query would have
    /// called that store idle and let the repair write it out from under the engine.
    /// </summary>
    [Fact]
    public void AStoreHeldAtAParkIsStillLive()
    {
        var repo = Path.Combine(_tmp, "parked-repo");
        var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
        Directory.CreateDirectory(stateDir);
        var db = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            Seed(store, "run-parked", repo);
            store.UpdateRunStatus("run-parked", "needs_human");
        }

        var me = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(stateDir, Conductor.Core.EngineLock.FileName),
            me.Id + Environment.NewLine + me.StartTime.ToUniversalTime().ToString("o"));

        var surveyed = Assert.Single(StateRepair.Survey(Root).Stores,
            s => StateMigration.PathsEqual(s.Db, db));
        Assert.True(surveyed.Live,
            "a run parked at needs_human under a live engine is still a store nothing may write");
    }

    // ───────────────────────────────────────────────────────────────────────── close and adopt

    /// <summary>A run that died on the 5th did not end on the 13th because that is when someone
    /// noticed. Every duration and cost-per-hour figure computed from the row afterwards inherits the
    /// difference, so the closure stamps the last thing the run is recorded as having done.</summary>
    [Fact]
    public void ClosingStampsTheInstantTheRunActuallyStopped()
    {
        var stopped = new DateTimeOffset(2026, 8, 5, 21, 40, 0, TimeSpan.Zero);
        var db = SeedCatalogued("phantom-repo", "run-phantom", stopped);

        var match = Assert.Single(RunRecordMaintenance.Find(Root, "run-phantom"));
        Assert.Equal("running", match.Status);
        Assert.Equal(stopped, RunRecordMaintenance.LastActivityUtc(match.Db, "run-phantom"));

        var outcome = RunRecordMaintenance.Close(match, RunRecord.Closed, stopped, "tester@rig", "engine killed",
                                                 TimeProvider.System);
        Assert.True(outcome.Ok, outcome.Message);

        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        var row = Assert.Single(store.Query("SELECT status, ended_utc FROM runs WHERE run_id = 'run-phantom'"));
        Assert.Equal("closed", row["status"]);
        Assert.Equal(stopped.ToString("O"), row["ended_utc"]);
    }

    /// <summary>Hand SQL leaves nothing behind saying who ran it. The verb writes the change into the
    /// event spine, where it outlives the process and reaches every reader of the run.</summary>
    [Fact]
    public void TheChangeSaysWhoMadeItAndWhy()
    {
        var db = SeedCatalogued("prov-repo", "run-prov", DateTimeOffset.UtcNow);
        var match = Assert.Single(RunRecordMaintenance.Find(Root, "run-prov"));

        Assert.True(RunRecordMaintenance.Close(match, RunRecord.Closed, DateTimeOffset.UtcNow,
                                               "tester@rig", "window closed on 5 Aug", TimeProvider.System).Ok);

        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        var notes = store.Query("SELECT payload FROM events WHERE run_id = 'run-prov' AND type = 'NoteAdded'");
        var payload = Assert.Single(notes)["payload"]!.ToString()!;
        Assert.Contains("window closed on 5 Aug", payload, StringComparison.Ordinal);
        Assert.Contains("tester@rig", payload, StringComparison.Ordinal);
        // "closed: running -> closed" — the arrow is > once the payload is JSON, so the prefix
        // is what this asserts on.
        Assert.Contains("closed: running", payload, StringComparison.Ordinal);
    }

    /// <summary>Adopt annotates and stops. A record you have adopted is one you intend to keep, which
    /// is the opposite of closing it, so the status is left exactly where it was.</summary>
    [Fact]
    public void AdoptAnnotatesAndLeavesTheStatusAlone()
    {
        var db = SeedCatalogued("adopt-repo", "run-adopt", DateTimeOffset.UtcNow);
        var match = Assert.Single(RunRecordMaintenance.Find(Root, "run-adopt"));

        Assert.True(RunRecordMaintenance.Adopt(match, "tester@rig", "watched by the owner from here",
                                               TimeProvider.System).Ok);

        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        var row = Assert.Single(store.Query("SELECT status, ended_utc FROM runs WHERE run_id = 'run-adopt'"));
        Assert.Equal("running", row["status"]);
        Assert.Null(row["ended_utc"]);
        var payload = Assert.Single(store.Query(
            "SELECT payload FROM events WHERE run_id = 'run-adopt' AND type = 'NoteAdded'"))["payload"]!.ToString()!;
        Assert.Contains("adopted: watched by the owner from here", payload, StringComparison.Ordinal);
    }

    /// <summary>Rule 2, inherited from the repair pass and the reason a scratch rig cannot corrupt the
    /// run driving it: a store a live engine is using is never written, whatever the operator asked
    /// for.</summary>
    [Fact]
    public void AStoreALiveEngineIsUsingIsNeverWritten()
    {
        var repo = Path.Combine(_tmp, "live-repo");
        var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
        Directory.CreateDirectory(stateDir);
        var db = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance)) Seed(store, "run-live", repo);

        var me = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(stateDir, Conductor.Core.EngineLock.FileName),
            me.Id + Environment.NewLine + me.StartTime.ToUniversalTime().ToString("o"));

        var match = Assert.Single(RunRecordMaintenance.Find(Root, "run-live"));
        Assert.True(match.Live);

        var closed = RunRecordMaintenance.Close(match, RunRecord.Closed, DateTimeOffset.UtcNow, "tester@rig", null,
                                                TimeProvider.System);
        Assert.False(closed.Ok);
        Assert.Contains("live engine", closed.Message, StringComparison.Ordinal);
        Assert.False(RunRecordMaintenance.Adopt(match, "tester@rig", "mine now", TimeProvider.System).Ok);

        using var after = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        Assert.Equal("running",
            Assert.Single(after.Query("SELECT status FROM runs WHERE run_id = 'run-live'"))["status"]);
    }

    /// <summary>Only a terminal word closes a record. Closing a run as <c>paused</c> would put the row
    /// straight back into the class of lies this checkpoint exists to end.</summary>
    [Fact]
    public void ARecordIsNotClosedIntoAParkedState()
    {
        SeedCatalogued("bad-repo", "run-bad", DateTimeOffset.UtcNow);
        var match = Assert.Single(RunRecordMaintenance.Find(Root, "run-bad"));

        var outcome = RunRecordMaintenance.Close(match, "paused", DateTimeOffset.UtcNow, "tester@rig", null,
                                                 TimeProvider.System);
        Assert.False(outcome.Ok);
        Assert.Contains("not a closed state", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>Naming a run that is not in the store is an answer, not a silent success. Every other
    /// write in this store swallows a miss; a maintenance verb that reported "closed" for a run it
    /// never found would be worse than the hand SQL it replaces.</summary>
    [Fact]
    public void ClosingARunThatIsNotThereChangesNothingAndSaysSo()
    {
        var db = Path.Combine(_tmp, "s3", "run.db");
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        Seed(store, "run-present");

        Assert.Equal(0, store.CloseRunRecord("run-absent", RunRecord.Closed, DateTimeOffset.UtcNow));
        Assert.Equal(1, store.CloseRunRecord("run-present", RunRecord.Closed, DateTimeOffset.UtcNow));
    }

    /// <summary>A run id is printed short everywhere an operator reads one, so a prefix is accepted —
    /// but an ambiguous prefix is reported, never guessed at. Deleting the wrong run's ending is not
    /// a mistake that announces itself.</summary>
    [Fact]
    public void APrefixThatNamesTwoRunsIsReportedRatherThanGuessedAt()
    {
        SeedCatalogued("many-repo", "run-same-a", DateTimeOffset.UtcNow);
        SeedCatalogued("many-repo2", "run-same-b", DateTimeOffset.UtcNow);

        Assert.Equal(2, RunRecordMaintenance.Find(Root, "run-same").Count);
        Assert.Single(RunRecordMaintenance.Find(Root, "run-same-a"));
        Assert.Empty(RunRecordMaintenance.Find(Root, "run-nothing"));
    }

    // ────────────────────────────────────────────────────────────────────────────────── rig

    private static void Seed(SqliteRunStore store, string runId, string? repo = null)
    {
        store.SetRunId(runId);
        store.InitializeRun(runId, "core plan", repo ?? "C:/nowhere", "main",
                            Conductor.Core.EngineStamp.Parse("test"));
    }

    /// <summary>A phantom, made the way the real ones were made: a run row still saying
    /// <c>running</c>, a session that ended when the engine died, and nothing after it.</summary>
    private string SeedCatalogued(string repoName, string runId, DateTimeOffset lastActivity)
    {
        var repo = Path.Combine(_tmp, repoName);
        Directory.CreateDirectory(Path.Combine(repo, StateHome.ScratchDirName));
        var db = StateHome.Resolve(repo, "core plan", Root).RunDbPath;
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        Seed(store, runId, repo);
        store.InitializeStage(runId, "K1", "Stage One");
        store.RecordSession(runId, "K1", 1, "session",
                            lastActivity.UtcDateTime.AddHours(-1), lastActivity.UtcDateTime,
                            "Completed", null, 0, 1, null, null, 0, null);
        return db;
    }
}
