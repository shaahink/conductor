using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS1.2 — the archive's stage rows come from the event fold, not the <c>stages</c> side table.
///
/// <para>The side table's <c>session_count</c> column has had NO writer since v1 declared it: every
/// run that ever held a session reported 0 sessions per stage, and the column read as truth because
/// it sat in a table. The fold answers from the events that actually happened — the same move that
/// retired the mutable <c>checkpoints</c> table at schema v8 — and the parity test pins the derived
/// status to the status surface's own vocabulary so history and the dashboard cannot drift apart.</para>
/// </summary>
public sealed class KS1_2StagesFromFoldTests : IDisposable
{
    private readonly string _tmp;

    public KS1_2StagesFromFoldTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks12-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ the fold

    [Fact]
    public void StagesDeriveFromTheFold()
    {
        const string runId = "run-ks12-0001";
        var db = NewDb("fold");
        using (var store = Open(db, runId))
        {
            store.InitializeStage(runId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });
            store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "deliver" });
            store.Emit(new SessionStarted { Number = 2, StageId = "S1", Kind = "verify" });
            store.SeedCheckpoints(runId,
            [
                ("S1.1", "S1", "one", "DONE", "abc1234", "e1"),
                ("S2.1", "S2", "two", "TODO", "-", "-"),
            ]);
            store.ConfirmStage(runId, "S1");
            store.Emit(new StageConfirmed { StageId = "S1" });
            store.InitializeStage(runId, "S2", "Second stage");
            store.Emit(new StageEntered { StageId = "S2", Title = "Second stage" });
            store.Emit(new SessionStarted { Number = 3, StageId = "S2", Kind = "deliver" });
            // A nameless stage: StageEntered.Title is nullable and must degrade to the id.
            store.Emit(new StageEntered { StageId = "S3" });
            store.FlushEvents();
        }
        SqliteConnection.ClearAllPools();

        var stages = RunArchive.TryOpen(db)!.Stages(runId);

        Assert.Equal(["S1", "S2", "S3"], stages.Select(s => s.Id));

        var s1 = stages[0];
        Assert.Equal("First stage", s1.Title);
        Assert.Equal("confirmed", s1.Status);
        Assert.Equal(2, s1.Sessions);
        Assert.NotNull(s1.StartedUtc);
        Assert.NotNull(s1.ConfirmedUtc);

        var s2 = stages[1];
        Assert.Equal("Second stage", s2.Title);
        Assert.Equal("todo", s2.Status);      // open checkpoint, not current, not confirmed
        Assert.Equal(1, s2.Sessions);

        var s3 = stages[2];
        Assert.Equal("S3", s3.Title);          // the id, never a blank cell
        Assert.Equal("active", s3.Status);     // the last stage entered
        Assert.Equal(0, s3.Sessions);
        Assert.Null(s3.ConfirmedUtc);
    }

    [Fact]
    public void SessionCountIsDerivedNotRead()
    {
        const string runId = "run-ks12-0002";
        var db = NewDb("count");
        using (var store = Open(db, runId))
        {
            store.InitializeStage(runId, "S1", "Only stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "Only stage" });
            for (var i = 1; i <= 3; i++)
                store.Emit(new SessionStarted { Number = i, StageId = "S1", Kind = "deliver" });
            store.FlushEvents();
        }
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db)!;
        Assert.Equal(3, Assert.Single(archive.Stages(runId)).Sessions);

        // And the side table really does still say 0 for the same stage — the recorded lie this
        // fold retires. Read raw here, in a test: shipped code may not (the KS1_2 scan).
        var raw = archive.Query("SELECT session_count FROM stages WHERE run_id = @r AND id = 'S1'", ("@r", runId));
        Assert.Equal(0L, Convert.ToInt64(Assert.Single(raw)["session_count"],
            System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ parity with the status surface

    /// <summary>The derived status must be the STATUS SURFACE's answer, not a lookalike. For every
    /// run in the corpus the archive's word is compared against <c>SnapshotBuilder</c> — the thing
    /// the dashboard and <c>preview</c> render — fed from the same log via
    /// <c>RunStateProjection.Fold</c> and the store's own checkpoint fold.</summary>
    [Fact]
    public void DerivedStatusMatchesTheStatusSurface_ForEverySeededRun()
    {
        // Three runs: mid-flight (confirmed/todo/active), settled-unconfirmed (done), and
        // in-progress-current (active over an open card).
        var corpus = new (string RunId, Action<SqliteRunStore, string> Seed)[]
        {
            ("run-par-0001", SeedMidFlight),
            ("run-par-0002", SeedSettledUnconfirmed),
            ("run-par-0003", SeedActiveOverOpenCard),
        };

        foreach (var (runId, seed) in corpus)
        {
            var db = NewDb("par-" + runId[^4..]);
            IReadOnlyList<ConductorEvent> events;
            TrackerSnapshot track;
            using (var store = Open(db, runId))
            {
                seed(store, runId);
                store.FlushEvents();
                events = store.ReadAllEvents(runId);
                track = new TrackerSnapshot
                {
                    Checkpoints = store.GetCheckpoints(runId)
                        .Select(c => new Conductor.Core.CheckpointRow(c.Id, c.Title, c.Status, c.Commit, c.Evidence)
                        {
                            StageId = c.StageId,
                        }).ToList(),
                };
            }
            SqliteConnection.ClearAllPools();

            var state = RunStateProjection.Fold(events);
            var plan = new PlanConfig
            {
                Name = "ks12-parity",
                Repo = _tmp.Replace('\\', '/'),
                Stages =
                [
                    new StageConfig { Id = "S1", Title = "one", Sessions = 1 },
                    new StageConfig { Id = "S2", Title = "two", Sessions = 1 },
                    new StageConfig { Id = "S3", Title = "three", Sessions = 1 },
                ],
            };
            var surface = SnapshotBuilder.Build(plan, state, track);

            var archived = RunArchive.TryOpen(db)!.Stages(runId);
            Assert.NotEmpty(archived);
            foreach (var s in archived)
            {
                var expected = surface.Stages.Single(x => x.Id == s.Id);
                Assert.Equal(expected.State, s.Status);
                Assert.Equal(expected.Attempts, s.Sessions);
            }
        }
    }

    private static void SeedMidFlight(SqliteRunStore store, string runId)
    {
        store.InitializeStage(runId, "S1", "one");
        store.Emit(new StageEntered { StageId = "S1", Title = "one" });
        store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "deliver" });
        store.SeedCheckpoints(runId,
        [
            ("S1.1", "S1", "a", "DONE", "abc", "e"),
            ("S2.1", "S2", "b", "TODO", "-", "-"),
            ("S3.1", "S3", "c", "TODO", "-", "-"),
        ]);
        store.Emit(new StageConfirmed { StageId = "S1" });
        store.Emit(new StageEntered { StageId = "S2", Title = "two" });
        store.Emit(new SessionStarted { Number = 2, StageId = "S2", Kind = "deliver" });
        store.Emit(new StageEntered { StageId = "S3", Title = "three" });
    }

    private static void SeedSettledUnconfirmed(SqliteRunStore store, string runId)
    {
        // Every card done, no StageConfirmed: the surface says "done" (the plan's gate policy is the
        // default perSession, so no "gating") and the fold must say the same — checked BEFORE
        // "active" even though this is the current stage, the surface's own precedence.
        store.InitializeStage(runId, "S1", "one");
        store.Emit(new StageEntered { StageId = "S1", Title = "one" });
        store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "deliver" });
        store.SeedCheckpoints(runId, [("S1.1", "S1", "a", "DONE", "abc", "e")]);
    }

    private static void SeedActiveOverOpenCard(SqliteRunStore store, string runId)
    {
        store.InitializeStage(runId, "S1", "one");
        store.Emit(new StageEntered { StageId = "S1", Title = "one" });
        store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "deliver" });
        store.Emit(new SessionStarted { Number = 2, StageId = "S1", Kind = "deliver" });
        store.SeedCheckpoints(runId, [("S1.1", "S1", "a", "IN PROGRESS", "-", "-")]);
    }

    // ------------------------------------------------------------------ degradation

    /// <summary>A pre-v5 import has no <c>events</c> table at all. The archive must answer an empty
    /// stage list — never a throw that takes the whole history view down — even when the old side
    /// table still holds rows nobody can vouch for.</summary>
    [Fact]
    public void ArchiveWithNoEventLog_ListsEmptyStages()
    {
        var db = Path.Combine(_tmp, "ancient", "run.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE runs (run_id TEXT PRIMARY KEY, plan_name TEXT, repo TEXT, branch TEXT," +
                " driver_ver TEXT, status TEXT, started_utc TEXT, ended_utc TEXT);" +
                "INSERT INTO runs VALUES ('run-ancient-01','core','C:\\old','master','0.1.0','completed'," +
                " '2025-11-01T00:00:00Z','2025-11-02T00:00:00Z');" +
                "CREATE TABLE stages (id TEXT, run_id TEXT, title TEXT, status TEXT," +
                " session_count INTEGER, started_utc TEXT, confirmed_utc TEXT);" +
                "INSERT INTO stages VALUES ('F1','run-ancient-01','Old stage','done',0," +
                " '2025-11-01T01:00:00Z','2025-11-01T02:00:00Z');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);
        Assert.Empty(archive!.Stages("run-ancient-01"));
        Assert.Empty(archive.Checkpoints("run-ancient-01"));  // the shared reader degrades both folds
    }

    /// <summary>One torn event must not take the listing down — the same tolerance the checkpoint
    /// fold has always kept for a payload that does not parse.</summary>
    [Fact]
    public void TornEventsAreSkippedNotFatal()
    {
        const string runId = "run-ks12-0004";
        var db = NewDb("torn");
        using (var store = Open(db, runId))
        {
            store.InitializeStage(runId, "S1", "Only stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "Only stage" });
            store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "deliver" });
            store.FlushEvents();
        }
        SqliteConnection.ClearAllPools();

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO events (seq, ts, run_id, session_id, type, payload) " +
                              "VALUES (999, '2026-08-13T00:00:00Z', @r, NULL, 'StageEntered', '{ torn')";
            cmd.Parameters.AddWithValue("@r", runId);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var stage = Assert.Single(RunArchive.TryOpen(db)!.Stages(runId));
        Assert.Equal("S1", stage.Id);
        Assert.Equal(1, stage.Sessions);
    }

    // ------------------------------------------------------------------ fixtures

    private string NewDb(string name)
    {
        var dir = Path.Combine(_tmp, name);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "run.db");
    }

    private static SqliteRunStore Open(string db, string runId)
    {
        var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        store.InitializeRun(runId, "core", Path.GetDirectoryName(db)!, "master",
            EngineStamp.Parse("0.3.1-alpha+test"));
        store.SetRunId(runId);
        return store;
    }
}
