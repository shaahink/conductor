using Conductor.Core;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K3.3 — a run record that says which engine produced it and under which limits.
///
/// <para>Before this, <c>runs.driver_ver</c> held <c>Assembly.GetName().Version</c>: the same
/// <c>2.0.0.0</c> for every build this repo has ever produced, so a run executed on an uncommitted
/// working-tree build was indistinguishable in the record from one executed on a released tag. The
/// limits were not recorded at all, which is why "the cap was raised at session 9" is currently a
/// deduction from the shape of a token curve.</para>
///
/// <para>The tests that matter here are the two about CHANGE — limits edited mid-run, engine swapped
/// between sessions — because a single run-level snapshot would pass a naive test and still answer
/// the real question wrongly.</para>
/// </summary>
public sealed class K3_3ProvenanceTests : IDisposable
{
    private readonly string _tmp;

    public K3_3ProvenanceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-k33-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Db(string name = "run.db")
    {
        var path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(_tmp);
        return path;
    }

    private static SqliteRunStore Open(string path) => new(path, NullLogger<SqliteRunStore>.Instance);

    // ------------------------------------------------------------------ the limits snapshot

    [Fact]
    public void Snapshot_records_the_effective_nudge_ratio_not_the_unset_field()
    {
        // SoftBreakRatio null does NOT mean "no nudge" — PromptBuilder.Budget.cs:25 falls back to 0.8
        // and nudges anyway. Recording the raw null would put "never nudged" in the record of a
        // session that was.
        var snap = RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = 32_000_000 });

        Assert.Equal(0.8, snap.NudgeRatio);
        Assert.Equal(25_600_000, snap.NudgeTokens);

        var explicitly = RunLimitsSnapshot.From(
            new LimitsConfig { MaxSessionTokens = 32_000_000, SoftBreakRatio = 0.7 });
        Assert.Equal(0.7, explicitly.NudgeRatio);
        Assert.Equal(22_400_000, explicitly.NudgeTokens);   // this era's real nudge point
    }

    [Fact]
    public void Snapshot_of_an_uncapped_run_has_no_ratio_to_record()
    {
        var snap = RunLimitsSnapshot.From(new LimitsConfig { SoftBreakRatio = 0.7 });

        Assert.Null(snap.SessionTokenCap);
        Assert.Null(snap.NudgeRatio);      // a fraction of nothing is not 0.7 of anything
        Assert.Null(snap.NudgeTokens);
        Assert.Equal(2, snap.LaneConcurrency);
        Assert.Contains("cap none", snap.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_round_trips_through_the_column_it_is_stored_in()
    {
        var snap = RunLimitsSnapshot.From(new LimitsConfig
        {
            MaxSessionTokens = 32_000_000,
            SoftBreakRatio = 0.7,
            MaxRunCostUsd = 400m,
            MaxRunTokens = 900_000_000,
            MaxSessions = 40,
            MaxConcurrentLanes = 3,
        });

        var back = RunLimitsSnapshot.FromJson(snap.ToJson());

        Assert.Equal(snap, back);
        Assert.Equal("cap 32M · nudge 0.7 (22.4M) · run ≤ $400 · run ≤ 900M · 40 sessions · lanes 3",
            back!.Describe());
    }

    [Fact]
    public void A_torn_or_absent_snapshot_reads_as_unrecorded_rather_than_throwing()
    {
        Assert.Null(RunLimitsSnapshot.FromJson(null));
        Assert.Null(RunLimitsSnapshot.FromJson("   "));
        Assert.Null(RunLimitsSnapshot.FromJson("{\"sessionTokenCap\": tru"));
    }

    // ------------------------------------------------------------------ the engine stamp

    [Fact]
    public void The_stamp_round_trips_and_keeps_the_dirty_flag()
    {
        var dirty = new EngineStamp("0.3.1-alpha.0.6", "98a426af63d6", true);
        Assert.Equal("0.3.1-alpha.0.6+98a426af63d6.dirty", dirty.Full);
        Assert.Equal(dirty, EngineStamp.Parse(dirty.Full));

        var clean = new EngineStamp("0.3.1", "abc123", false);
        Assert.Equal("0.3.1+abc123", clean.Full);
        Assert.Equal(clean, EngineStamp.Parse(clean.Full));

        // A bare version — what an old row or a test fixture carries — keeps its commit honest.
        Assert.Equal(new EngineStamp("1.0", BuildInfo.UnknownCommit, false), EngineStamp.Parse("1.0"));
        Assert.Equal("1.0", EngineStamp.Parse("1.0").Full);
    }

    [Fact]
    public void The_running_engine_stamps_itself_from_its_assembly()
    {
        // Not a tautology: the point is that BuildInfo.Current is what gets persisted, so this
        // asserts the stamp is non-empty and matches the doctor surface rather than a constant.
        var stamp = EngineStamp.Current;
        Assert.False(string.IsNullOrWhiteSpace(stamp.Version));
        Assert.Equal(BuildInfo.Current.Version, stamp.Version);
        Assert.Equal(BuildInfo.Current.CommitSha, stamp.Commit);
        Assert.Equal(BuildInfo.Current.Dirty, stamp.Dirty);
    }

    // ------------------------------------------------------------------ what the store writes

    [Fact]
    public void A_run_row_carries_the_engine_that_wrote_it()
    {
        var db = Db();
        using (var store = Open(db))
        {
            store.InitializeRun("run-k33-0001", "core", "C:\\repo", "feat/karvan",
                new EngineStamp("0.3.1-alpha.0.6", "98a426af63d6", true),
                RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = 32_000_000, SoftBreakRatio = 0.7 }).ToJson());
        }
        SqliteConnection.ClearAllPools();

        var run = Assert.Single(RunArchive.TryOpen(db)!.Runs());
        Assert.Equal("0.3.1-alpha.0.6", run.EngineVersion);
        Assert.Equal("98a426af63d6", run.EngineCommit);
        Assert.True(run.EngineDirty);
        Assert.Equal("0.3.1-alpha.0.6+98a426af63d6.dirty", run.EngineStampText);
        Assert.Equal(32_000_000, run.Limits!.SessionTokenCap);
        Assert.Equal(0.7, run.Limits.NudgeRatio);
    }

    [Fact]
    public void Resuming_a_run_keeps_its_original_start_and_restamps_the_engine()
    {
        // InitializeRun runs on EVERY process start. It used to be INSERT OR REPLACE, which rewrote
        // the whole row — so a run resumed after a crash reported that it had begun moments ago, and
        // every duration derived from the record was the last process's, not the run's.
        var db = Db();
        string firstStart;
        using (var store = Open(db))
        {
            store.InitializeRun("run-k33-0002", "core", "C:\\repo", "feat/karvan",
                new EngineStamp("0.3.0", "aaaaaa", false),
                RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = 24_000_000 }).ToJson());
            firstStart = (string)RunArchive.TryOpen(db)!.Query(
                "SELECT started_utc FROM runs")[0]["started_utc"]!;

            store.InitializeRun("run-k33-0002", "core", "C:\\repo", "feat/karvan",
                new EngineStamp("0.3.1", "bbbbbb", true),
                RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = 32_000_000 }).ToJson());
        }
        SqliteConnection.ClearAllPools();

        var run = Assert.Single(RunArchive.TryOpen(db)!.Runs());
        Assert.Equal(firstStart, run.StartedUtc);
        Assert.Equal("0.3.1+bbbbbb.dirty", run.EngineStampText);   // what is driving it NOW
        Assert.Equal(32_000_000, run.Limits!.SessionTokenCap);
    }

    [Fact]
    public void Every_session_carries_the_limits_in_force_when_it_ran()
    {
        // The Sarban case, reproduced: nine sessions under one cap, the rest under a raised one. A
        // run-level snapshot alone answers "32M" for all of them, which is false for sessions 1-8.
        var db = Db();
        var lowCap = RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = 24_000_000, SoftBreakRatio = 0.7 }).ToJson();
        var highCap = RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = 32_000_000, SoftBreakRatio = 0.7 }).ToJson();
        using (var store = Open(db))
        {
            store.InitializeRun("run-k33-0003", "core", "C:\\repo", "master", new EngineStamp("0.3.0", "aaaaaa", false), lowCap);
            store.InitializeStage("run-k33-0003", "S1", "stage one");
            for (var i = 1; i <= 10; i++)
            {
                store.RecordSession("run-k33-0003", "S1", i, "work",
                    new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc).AddHours(i),
                    "advance", agentSessionId: null, resumeCount: 0, attempt: 1,
                    gateSummary: null, resultSummary: null, commitCount: 1, newlyDone: null,
                    digest: null, softBreak: null,
                    engine: i < 9 ? "0.3.0+aaaaaa" : "0.3.1+bbbbbb.dirty",
                    limits: i < 9 ? lowCap : highCap);
            }
        }
        SqliteConnection.ClearAllPools();

        var sessions = RunArchive.TryOpen(db)!.Sessions("run-k33-0003");
        Assert.Equal(10, sessions.Count);
        Assert.Equal(24_000_000, sessions[0].Limits!.SessionTokenCap);
        Assert.Equal(24_000_000, sessions[7].Limits!.SessionTokenCap);   // session 8
        Assert.Equal(32_000_000, sessions[8].Limits!.SessionTokenCap);   // session 9 — the raise
        Assert.Equal("0.3.0+aaaaaa", sessions[7].Engine);
        Assert.Equal("0.3.1+bbbbbb.dirty", sessions[8].Engine);

        // And the moment is findable without reading ten rows by eye.
        var raised = sessions.First(s => s.Limits?.SessionTokenCap == 32_000_000).Number;
        Assert.Equal(9, raised);
    }

    // ------------------------------------------------------------------ older databases

    [Fact]
    public void A_database_without_the_provenance_columns_still_reads()
    {
        // RunArchive opens databases this engine did not write — v9 imports, other machines. A SELECT
        // naming engine_commit on one of those throws "no such column" and takes the whole listing
        // down, so every new column is read through a table_info probe.
        var db = Db("v10.db");
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE schema_version (version INTEGER NOT NULL);" +
                "INSERT INTO schema_version (version) VALUES (10);" +
                "CREATE TABLE runs (run_id TEXT PRIMARY KEY, plan_name TEXT, repo TEXT, branch TEXT," +
                " driver_ver TEXT, status TEXT, started_utc TEXT, ended_utc TEXT);" +
                "INSERT INTO runs VALUES ('run-old-000001','core','C:\\old','master','2.0.0.0','completed'," +
                " '2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');" +
                "CREATE TABLE sessions (run_id TEXT, number INTEGER, stage_id TEXT, kind TEXT," +
                " started_utc TEXT, ended_utc TEXT, outcome TEXT, attempt INTEGER, resume_count INTEGER," +
                " commit_count INTEGER, result_summary TEXT, gate_summary TEXT);" +
                "INSERT INTO sessions VALUES ('run-old-000001',1,'S1','work','2026-01-01T00:00:00Z'," +
                " '2026-01-01T01:00:00Z','advance',1,0,1,'did a thing','ok');" +
                "CREATE TABLE costs (run_id TEXT, session_number INTEGER, category TEXT, tokens_in INTEGER," +
                " tokens_out INTEGER, tokens_think INTEGER, tokens_cache INTEGER, cost_usd REAL);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db);
        var run = Assert.Single(archive!.Runs());

        Assert.Equal("2.0.0.0", run.EngineVersion);     // the useless answer, printed as it is
        Assert.Null(run.EngineCommit);
        Assert.Null(run.EngineDirty);
        Assert.Null(run.Limits);
        Assert.Equal("2.0.0.0", run.EngineStampText);   // no invented commit
        var session = Assert.Single(archive.Sessions("run-old-000001"));
        Assert.Null(session.Engine);
        Assert.Null(session.Limits);
    }

    [Fact]
    public void A_v10_database_migrates_to_v11_and_keeps_its_rows()
    {
        // Built by taking a real v11 database back to v10 — dropping the columns the migration adds
        // and resetting the stored version — so the upgrade under test is the shipped .sql file and
        // not a hand-written approximation of it.
        var db = Db("upgrade.db");
        using (var store = Open(db))
            store.InitializeRun("run-k33-0004", "core", "C:\\repo", "master", new EngineStamp("0.3.0", "aaaaaa", false));
        SqliteConnection.ClearAllPools();

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "ALTER TABLE runs DROP COLUMN engine_version;" +
                "ALTER TABLE runs DROP COLUMN engine_commit;" +
                "ALTER TABLE runs DROP COLUMN engine_dirty;" +
                "ALTER TABLE runs DROP COLUMN limits_json;" +
                "ALTER TABLE sessions DROP COLUMN engine;" +
                "ALTER TABLE sessions DROP COLUMN limits;" +
                "UPDATE schema_version SET version = 10;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using (var store = Open(db))
            store.RecordRunEnd("run-k33-0004", "completed");
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db)!;
        Assert.Equal((long)SqliteRunStore.CurrentSchemaVersion,
            Convert.ToInt64(archive.Query("SELECT version FROM schema_version")[0]["version"]));
        Assert.Equal(11, SqliteRunStore.CurrentSchemaVersion);
        var run = Assert.Single(archive.Runs());
        Assert.Equal("run-k33-0004", run.RunId);
        Assert.Equal("completed", run.Status);
        Assert.Null(run.EngineCommit);   // the column is back; the pre-migration row never had a value
    }

    [Fact]
    public void A_fresh_database_has_the_provenance_columns()
    {
        var db = Db("fresh.db");
        using (var store = Open(db))
            store.InitializeRun("run-k33-0005", "core", "C:\\repo", "master", EngineStamp.Current);
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db)!;
        var runCols = archive.Query("PRAGMA table_info(runs)").Select(r => (string)r["name"]!).ToList();
        var sessionCols = archive.Query("PRAGMA table_info(sessions)").Select(r => (string)r["name"]!).ToList();

        Assert.Contains("engine_version", runCols, StringComparer.Ordinal);
        Assert.Contains("engine_commit", runCols, StringComparer.Ordinal);
        Assert.Contains("engine_dirty", runCols, StringComparer.Ordinal);
        Assert.Contains("limits_json", runCols, StringComparer.Ordinal);
        Assert.Contains("engine", sessionCols, StringComparer.Ordinal);
        Assert.Contains("limits", sessionCols, StringComparer.Ordinal);

        // driver_ver survives for old readers, and now carries the whole stamp instead of 2.0.0.0.
        Assert.Equal(EngineStamp.Current.Full,
            archive.Query("SELECT driver_ver FROM runs")[0]["driver_ver"] as string);
    }
}
