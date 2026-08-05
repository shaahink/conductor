using Conductor.Core.Fleet;
using Conductor.Core.History;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K3.2 — the machine's history is readable, and reading it cannot change it.
///
/// <para>Every test drives its own state-home root explicitly and never touches the operator's real
/// history. The read-only claim is not asserted from source: two tests make the archive attempt a
/// write and prove SQLite refuses, and one opens a database at an OLDER schema version and proves
/// browsing leaves that version alone — which is exactly what pointing <c>SqliteRunStore</c> at an
/// archived run would not do.</para>
/// </summary>
public sealed class K3_2HistoryTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    public K3_2HistoryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-k32-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Writes a real run through the real writer, then catalogues it — so what the archive
    /// reads is what the engine actually stores, not a hand-rolled table.</summary>
    private string SeedRun(
        string repo, string plan, string runId, DateTime startedUtc,
        string status = "completed", int sessions = 2, decimal costPerSession = 1.5m)
    {
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", Conductor.Core.EngineStamp.Parse("0.3.1-alpha+test"));
            store.InitializeStage(runId, "S1", "First stage");
            for (var i = 1; i <= sessions; i++)
            {
                store.RecordSession(runId, "S1", i, "work",
                    startedUtc.AddHours(i), startedUtc.AddHours(i).AddMinutes(30), "advance",
                    agentSessionId: null, resumeCount: 0, attempt: 1,
                    gateSummary: "ok", resultSummary: $"session {i}", commitCount: i, newlyDone: null);
                store.RecordCost(runId, i, "agent", 100, 200, 0, 300, costPerSession, 1000);
            }
            store.SeedCheckpoints(runId,
            [
                ("C1", "S1", "First checkpoint", "DONE", "abc1234", "evidence/one.md"),
                ("C2", "S1", "Second checkpoint", "TODO", "-", "-"),
            ]);
            if (status != "running") store.RecordRunEnd(runId, status);
        }
        StateCatalogue.Upsert(_root, repo, plan, db);
        return db;
    }

    private string RepoPath(string name)
    {
        var p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(p);
        return p;
    }

    // ------------------------------------------------------------------ listing

    [Fact]
    public void List_returns_every_catalogued_run_newest_activity_first()
    {
        var older = RepoPath("older");
        var newer = RepoPath("newer");
        SeedRun(older, "core", "run-older-0001", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        SeedRun(newer, "core", "run-newer-0002", new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        var rows = RunHistory.List(_root);

        Assert.Equal(2, rows.Count);
        Assert.Equal("run-newer-0002", rows[0].Run!.RunId);
        Assert.Equal("run-older-0001", rows[1].Run!.RunId);
        Assert.All(rows, r => Assert.True(r.Readable));
    }

    [Fact]
    public void List_carries_outcome_sessions_and_cost_for_each_run()
    {
        var repo = RepoPath("costed");
        SeedRun(repo, "core", "run-costed-0001", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            status: "completed", sessions: 3, costPerSession: 2m);

        var row = Assert.Single(RunHistory.List(_root));

        Assert.Equal("completed", row.Run!.Status);
        Assert.Equal(3, row.Run.Sessions);
        Assert.Equal(6m, row.Run.CostUsd);
        Assert.Equal(1800, row.Run.Tokens); // 3 sessions x (100 + 200 + 0 + 300)
        // K3.3 split the stamp: EngineVersion is the version alone, EngineStampText is the whole
        // thing. The fixture seeds "0.3.1-alpha+test", so both halves are checked here.
        Assert.Equal("0.3.1-alpha", row.Run.EngineVersion);
        Assert.Equal("0.3.1-alpha+test", row.Run.EngineStampText);
        Assert.Equal("core", row.Plan);
    }

    [Fact]
    public void List_reports_a_catalogued_database_that_is_gone_instead_of_hiding_it()
    {
        var repo = RepoPath("vanished");
        var db = SeedRun(repo, "core", "run-vanished-01", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SqliteConnection.ClearAllPools();
        File.Delete(db);

        var row = Assert.Single(RunHistory.List(_root));

        Assert.False(row.Readable);
        Assert.Null(row.Run);
        Assert.Equal(db, row.RunDbPath);
    }

    [Fact]
    public void List_filters_by_repo_by_plan_and_by_since()
    {
        var a = RepoPath("alpha");
        var b = RepoPath("beta");
        SeedRun(a, "core", "run-alpha-0001", new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        SeedRun(b, "lanes", "run-beta-00002", new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("run-alpha-0001",
            Assert.Single(RunHistory.List(_root, new RunHistoryFilter(Repo: a))).Run!.RunId);
        Assert.Equal("run-beta-00002",
            Assert.Single(RunHistory.List(_root, new RunHistoryFilter(Plan: "lanes"))).Run!.RunId);
        Assert.Equal("run-beta-00002",
            Assert.Single(RunHistory.List(_root,
                new RunHistoryFilter(Since: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)))).Run!.RunId);
        Assert.Empty(RunHistory.List(_root, new RunHistoryFilter(Repo: a, Plan: "lanes")));
    }

    [Fact]
    public void Find_resolves_a_run_id_prefix_a_slug_and_a_repo_name_and_refuses_to_guess()
    {
        var a = RepoPath("pickme");
        var b = RepoPath("other");
        SeedRun(a, "core", "run-aaaa-0001", new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        SeedRun(b, "core", "run-aaaa-0002", new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("run-aaaa-0001", RunHistory.Find(_root, "run-aaaa-0001", out _)!.Run!.RunId);
        Assert.Equal("run-aaaa-0001", RunHistory.Find(_root, "pickme", out _)!.Run!.RunId);
        Assert.Equal("run-aaaa-0001",
            RunHistory.Find(_root, StateHome.SlugFor(a, "core"), out _)!.Run!.RunId);

        // A prefix both runs share must not silently pick the newest one.
        Assert.Null(RunHistory.Find(_root, "run-aaaa", out var ambiguous));
        Assert.Equal(2, ambiguous.Count);
    }

    [Theory]
    [InlineData("7d", "2026-07-29T12:00:00Z")]
    [InlineData("2w", "2026-07-22T12:00:00Z")]
    [InlineData("3mo", "2026-05-05T12:00:00Z")]
    [InlineData("1y", "2025-08-05T12:00:00Z")]
    [InlineData("12h", "2026-08-05T00:00:00Z")]
    [InlineData("2026-07-01", "2026-07-01T00:00:00Z")]
    public void ParseSince_understands_the_windows_an_operator_types(string text, string expected)
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            RunHistory.ParseSince(text, now));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("7q")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseSince_refuses_what_it_does_not_understand(string? text)
    {
        // Null rather than "everything": a --since the engine silently ignored would answer a
        // question the operator did not ask.
        Assert.Null(RunHistory.ParseSince(text, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CheckpointCounts_reports_done_over_total()
    {
        var repo = RepoPath("counted");
        SeedRun(repo, "core", "run-counted-0001", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal((1, 2), RunHistory.CheckpointCounts(Assert.Single(RunHistory.List(_root))));
    }

    // ------------------------------------------------------------------ the spine

    [Fact]
    public void Archive_replays_the_spine_of_one_run()
    {
        var repo = RepoPath("spine");
        var db = SeedRun(repo, "core", "run-spine-0001", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));

        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);

        var stage = Assert.Single(archive!.Stages("run-spine-0001"));
        Assert.Equal("S1", stage.Id);
        Assert.Equal("First stage", stage.Title);

        var sessions = archive.Sessions("run-spine-0001");
        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, sessions[0].Number); // oldest first: the order it was lived in
        Assert.Equal("advance", sessions[0].Outcome);
        Assert.Equal(1.5m, sessions[0].CostUsd);
        Assert.Equal(600, sessions[0].Tokens);

        var checkpoints = archive.Checkpoints("run-spine-0001");
        Assert.Equal(2, checkpoints.Count);
        Assert.Equal("DONE", checkpoints.Single(c => c.Id == "C1").Status);
        Assert.Equal("TODO", checkpoints.Single(c => c.Id == "C2").Status);
        Assert.Equal("abc1234", checkpoints.Single(c => c.Id == "C1").Commit);
    }

    [Fact]
    public void Archive_refuses_a_file_that_is_not_a_run_database()
    {
        var junk = Path.Combine(_tmp, "notes.txt");
        File.WriteAllText(junk, "this is not sqlite");
        Assert.Null(RunArchive.TryOpen(junk));
        Assert.Null(RunArchive.TryOpen(Path.Combine(_tmp, "absent.db")));
    }

    // ------------------------------------------------------------------ read-only means read-only

    [Fact]
    public void Archive_cannot_write_because_sqlite_itself_refuses()
    {
        var repo = RepoPath("frozen");
        var db = SeedRun(repo, "core", "run-frozen-0001", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);

        var insert = Assert.Throws<SqliteException>(() =>
            archive!.Query("INSERT INTO runs (run_id, plan_name, repo, status, started_utc) " +
                           "VALUES ('forged', 'core', 'x', 'running', '2026-05-02T00:00:00Z')"));
        Assert.Contains("readonly", insert.Message, StringComparison.OrdinalIgnoreCase);

        var update = Assert.Throws<SqliteException>(() =>
            archive!.Query("UPDATE runs SET status = 'running' WHERE run_id = 'run-frozen-0001'"));
        Assert.Contains("readonly", update.Message, StringComparison.OrdinalIgnoreCase);

        // And the run is untouched.
        Assert.Equal("completed", Assert.Single(archive!.Runs()).Status);
    }

    [Fact]
    public void Browsing_an_older_schema_does_not_migrate_it()
    {
        // A run recorded by an engine two schema versions ago is exactly the run history exists to
        // preserve. SqliteRunStore would migrate it on open — three writes before the first read.
        var db = Path.Combine(_tmp, "ancient", "run.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE schema_version (version INTEGER NOT NULL);" +
                "INSERT INTO schema_version (version) VALUES (2);" +
                "CREATE TABLE runs (run_id TEXT PRIMARY KEY, plan_name TEXT, repo TEXT, branch TEXT," +
                " driver_ver TEXT, status TEXT, started_utc TEXT, ended_utc TEXT);" +
                "INSERT INTO runs VALUES ('run-ancient-01','core','C:\\old','master','0.1.0','completed'," +
                " '2025-11-01T00:00:00Z','2025-11-02T00:00:00Z');" +
                "CREATE TABLE sessions (run_id TEXT, number INTEGER, stage_id TEXT, kind TEXT," +
                " started_utc TEXT, ended_utc TEXT, outcome TEXT, attempt INTEGER, resume_count INTEGER," +
                " commit_count INTEGER, result_summary TEXT, gate_summary TEXT);" +
                "CREATE TABLE costs (run_id TEXT, session_number INTEGER, category TEXT, tokens_in INTEGER," +
                " tokens_out INTEGER, tokens_think INTEGER, tokens_cache INTEGER, cost_usd REAL);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var before = File.GetLastWriteTimeUtc(db);

        var archive = RunArchive.TryOpen(db);
        Assert.NotNull(archive);
        Assert.Equal("run-ancient-01", Assert.Single(archive!.Runs()).RunId);

        Assert.Equal(2L, Convert.ToInt64(archive.Query("SELECT version FROM schema_version")[0]["version"]));
        Assert.Equal(before, File.GetLastWriteTimeUtc(db));
        Assert.False(File.Exists(db + "-wal"), "a read-only open must not create a write-ahead log");
    }

    // ------------------------------------------------------------------ the Face's picker

    [Fact]
    public void The_face_envelope_carries_past_runs_and_never_the_one_that_is_live()
    {
        var a = RepoPath("livest");
        var b = RepoPath("finished");
        SeedRun(a, "core", "run-live-000001", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), status: "running");
        SeedRun(b, "core", "run-finished-01", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var past = FacePastRuns.Read(_root, ["run-live-000001"]);

        var one = Assert.Single(past);
        Assert.Equal("run-finished-01", one.RunId);
        Assert.Equal(1, one.Done);
        Assert.Equal(2, one.Total);

        // And it reaches the Face as `past`, beside the live runs rather than mixed into them.
        var json = FaceTarget.Serialize(
            [new FleetRun(4317, "http://127.0.0.1:4317", "core", "run-live-000001", a, a, "Running", "S1", "First", null, 0, 2, 0m)],
            new Dictionary<string, string>(StringComparer.Ordinal), a, past);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("run-live-000001", doc.RootElement.GetProperty("runs")[0].GetProperty("runId").GetString());
        Assert.Equal("run-finished-01", doc.RootElement.GetProperty("past")[0].GetProperty("runId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("past").GetArrayLength());
    }

    [Fact]
    public void The_face_envelope_caps_the_history_it_offers()
    {
        // The picker is a screen, not a report — beyond the cap the answer is `conductor history`.
        for (var i = 0; i < FacePastRuns.DefaultMax + 3; i++)
            SeedRun(RepoPath("r" + i), "core", $"run-many-{i:D6}",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), sessions: 1);

        Assert.Equal(FacePastRuns.DefaultMax, FacePastRuns.Read(_root).Count);
    }

    [Fact]
    public void Listing_does_not_restamp_the_catalogue_it_is_reading()
    {
        var repo = RepoPath("stable");
        SeedRun(repo, "core", "run-stable-0001", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = StateCatalogue.Read(_root).Single().LastSeenUtc;

        RunHistory.List(_root);
        RunHistory.Find(_root, "run-stable-0001", out _);

        Assert.Equal(before, StateCatalogue.Read(_root).Single().LastSeenUtc);
    }
}
