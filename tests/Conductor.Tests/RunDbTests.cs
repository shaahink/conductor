using System.Data;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F1.1 / M2: Proves the run.db schema auto-creates on open, SqliteRunStore write methods
/// persist data, and the Query surface returns correct results.
/// </summary>
public sealed class RunDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRunStore _db;

    public RunDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conductor-rundb-test-{Guid.NewGuid():N}.db");
        var logger = NullLogger<SqliteRunStore>.Instance;
        _db = new SqliteRunStore(_dbPath, logger);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Schema_creates_all_required_tables()
    {
        var tables = _db.Query(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        var names = tables.Select(r => (string)r["name"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("schema_version", names);
        Assert.Contains("runs", names);
        Assert.Contains("stages", names);
        Assert.Contains("sessions", names);
        Assert.Contains("attempts", names);
        Assert.Contains("gates", names);
        Assert.Contains("scores", names);
        Assert.Contains("ledger", names);
        Assert.Contains("handovers", names);
        Assert.Contains("injections", names);
        Assert.Contains("costs", names);
        Assert.Contains("checkpoints", names);
        Assert.Contains("pids", names);
        Assert.Contains("events", names);
        Assert.Contains("run_state", names);
        Assert.Contains("bugs", names);
    }

    [Fact]
    public void Schema_version_is_seven()
    {
        var rows = _db.Query("SELECT version FROM schema_version");
        Assert.Single(rows);
        Assert.Equal(7L, (long)rows[0]["version"]!);
    }

    [Fact]
    public void InitializeRun_writes_run_row()
    {
        _db.InitializeRun("r1", "test-plan", "/repo", "main", "1.0.0");

        var rows = _db.Query("SELECT * FROM runs WHERE run_id = 'r1'");
        Assert.Single(rows);
        Assert.Equal("test-plan", rows[0]["plan_name"]);
        Assert.Equal("/repo", rows[0]["repo"]);
        Assert.Equal("main", rows[0]["branch"]);
        Assert.Equal("running", rows[0]["status"]);
    }

    [Fact]
    public void Session_round_trip()
    {
        var started = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var ended = new DateTime(2026, 7, 10, 12, 5, 0, DateTimeKind.Utc);

        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.InitializeStage("r1", "F1", "test stage");
        _db.RecordSession("r1", "F1", 1, "Deliver",
            started, ended, "Advanced", "ses-abc", 0, 1,
            "build:OK · test:OK", "all good", 2, "F1.1");
        _db.RecordCost("r1", 1, "agent", 1000, 500, 200, 0, 0.05m, 300000);

        var sessions = _db.Query("SELECT * FROM sessions WHERE run_id = 'r1'");
        Assert.Single(sessions);
        Assert.Equal("F1", sessions[0]["stage_id"]);
        Assert.Equal(1L, sessions[0]["number"]);
        Assert.Equal("Deliver", sessions[0]["kind"]);
        Assert.Equal("Advanced", sessions[0]["outcome"]);
        Assert.Equal(2L, sessions[0]["commit_count"]);
        Assert.Equal("F1.1", sessions[0]["newly_done"]);

        var costs = _db.Query("SELECT * FROM costs WHERE run_id = 'r1'");
        Assert.Single(costs);
        Assert.Equal(1000L, costs[0]["tokens_in"]);
        Assert.Equal(0.05, (double)(costs[0]["cost_usd"] ?? 0.0), 3);
    }

    /// <summary>
    /// U2.2/U2.3: QuerySessions serves per-session cost + tokens for the Face's Report digest and Dev
    /// stats table. The sessions table stores NEITHER — they are summed from `costs`, which holds one
    /// row per category (agent | gate | advisor). This test gives session 1 THREE cost rows precisely
    /// so it fails if anyone "simplifies" the correlated subqueries into a JOIN: a join would emit the
    /// session three times and every consumer would silently read triple.
    /// </summary>
    [Fact]
    public void QuerySessions_sums_the_many_cost_rows_per_session_instead_of_joining_them()
    {
        var started = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.InitializeStage("r1", "F1", "test stage");
        _db.RecordSession("r1", "F1", 1, "Deliver", started, started.AddMinutes(5),
            "Advanced", "ses-abc", 0, 1, "build:OK", "ok", 2, "F1.1");
        _db.RecordCost("r1", 1, "agent", 1000, 500, 200, 4000, 0.05m, 300000);
        _db.RecordCost("r1", 1, "gate", 0, 0, 0, 0, 0.0025m, 1500);
        _db.RecordCost("r1", 1, "advisor", 100, 20, 0, 0, 0.01m, 900);

        var rows = _db.QuerySessions("r1");

        var s1 = Assert.Single(rows);
        Assert.Equal(1, s1.Number);
        Assert.Equal(0.0625, s1.CostUsd, 4); // 0.05 + 0.0025 + 0.01, summed across all categories
        Assert.Equal(1100, s1.TokensIn);     // agent + advisor; the gate row contributes zero
        Assert.Equal(520, s1.TokensOut);
        Assert.Equal(200, s1.TokensThink);
        Assert.Equal(4000, s1.TokensCache);
    }

    /// <summary>
    /// A session with no cost rows at all must read as zero, not throw and not vanish from the list —
    /// the Face renders every session, including one that died before recording anything.
    /// </summary>
    [Fact]
    public void QuerySessions_reports_zero_cost_for_a_session_with_no_cost_rows()
    {
        var started = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.InitializeStage("r1", "F1", "test stage");
        _db.RecordSession("r1", "F1", 1, "Deliver", started, null,
            "AgentError", null, 0, 1, null, null, 0, null);

        var s1 = Assert.Single(_db.QuerySessions("r1"));
        Assert.Equal(0, s1.CostUsd);
        Assert.Equal(0, s1.TokensIn);
    }

    /// <summary>
    /// Costs are keyed by (run_id, session_number), and session numbers restart per run — so another
    /// run's session 1 must not leak into this run's total.
    /// </summary>
    [Fact]
    public void QuerySessions_does_not_sum_another_runs_costs_into_this_run()
    {
        var started = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.InitializeStage("r1", "F1", "test stage");
        _db.RecordSession("r1", "F1", 1, "Deliver", started, started.AddMinutes(1),
            "Advanced", null, 0, 1, null, null, 0, null);
        _db.RecordCost("r1", 1, "agent", 10, 5, 0, 0, 0.01m, 100);
        // A different run that also has a session numbered 1.
        _db.InitializeRun("r2", "p", "/r", "b", "v");
        _db.RecordCost("r2", 1, "agent", 9999, 9999, 0, 0, 99.99m, 100);

        var s1 = Assert.Single(_db.QuerySessions("r1"));
        Assert.Equal(0.01, s1.CostUsd, 4);
        Assert.Equal(10, s1.TokensIn);
    }

    [Fact]
    public void Gate_record_round_trip()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.RecordGate("r1", 1, "F1", "build", "fast", "session", "abc123",
            passed: true, skipped: false, optional: false, exitCode: 0, durationMs: 1500, tail: "Build succeeded.");

        var gates = _db.Query("SELECT * FROM gates WHERE run_id = 'r1'");
        Assert.Single(gates);
        Assert.Equal("build", gates[0]["name"]);
        Assert.Equal("fast", gates[0]["tier"]);
        Assert.Equal(1L, gates[0]["passed"]);
        Assert.Equal(0L, gates[0]["skipped"]);
        Assert.Equal(1500L, gates[0]["duration_ms"]);
    }

    [Fact]
    public void Ledger_and_handover_round_trip()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.WriteLedger("r1", 1, "F1", "finding", "CT thread safety verified");
        _db.WriteHandover("r1", 1, "F1", "All gates green, ready for F2");

        var ledger = _db.Query("SELECT * FROM ledger WHERE run_id = 'r1'");
        Assert.Single(ledger);
        Assert.Equal("finding", ledger[0]["kind"]);
        Assert.Equal("CT thread safety verified", ledger[0]["content"]);

        var handovers = _db.Query("SELECT * FROM handovers WHERE run_id = 'r1'");
        Assert.Single(handovers);
        Assert.Equal("All gates green, ready for F2", handovers[0]["content"]);
    }

    [Fact]
    public void RecordRunEnd_updates_status()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.RecordRunEnd("r1", "Completed");

        var rows = _db.Query("SELECT status, ended_utc FROM runs WHERE run_id = 'r1'");
        Assert.Single(rows);
        Assert.Equal("Completed", rows[0]["status"]);
        Assert.NotNull(rows[0]["ended_utc"]);
    }

    [Fact]
    public void ConfirmStage_marks_done()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.InitializeStage("r1", "F1", "test");
        _db.ConfirmStage("r1", "F1");

        var rows = _db.Query("SELECT status, confirmed_utc FROM stages WHERE id = 'F1'");
        Assert.Single(rows);
        Assert.Equal("done", rows[0]["status"]);
    }

    [Fact]
    public void Injection_write_read()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.WriteInjection("r1", "human", 1, "F1", "Check the gate caching logic");

        var rows = _db.Query("SELECT * FROM injections WHERE run_id = 'r1'");
        Assert.Single(rows);
        Assert.Equal("human", rows[0]["kind"]);
        Assert.Equal(1L, rows[0]["source_session"]);
    }

    [Fact]
    public void GetLatestRunId_returns_latest_run()
    {
        _db.InitializeRun("run-a", "test-plan", "/r", "b", "v");
        System.Threading.Thread.Sleep(10);
        _db.InitializeRun("run-b", "test-plan", "/r", "b", "v");

        var runId = _db.GetLatestRunId("test-plan");
        Assert.Equal("run-b", runId);
    }

    [Fact]
    public void GetLatestRunId_returns_null_for_unknown_plan()
    {
        var runId = _db.GetLatestRunId("nonexistent-plan");
        Assert.Null(runId);
    }

    [Fact]
    public void Idempotent_schema_creation()
    {
        // Opening a second SqliteRunStore on the same file should not throw or duplicate tables
        var logger = NullLogger<SqliteRunStore>.Instance;
        using var db2 = new SqliteRunStore(_dbPath, logger);

        var tables = db2.Query(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        var names = tables.Select(r => (string)r["name"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sessions", names);
        Assert.Contains("gates", names);
    }

    [Fact]
    public void Query_with_parameters_prevents_injection()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.RecordGate("r1", 1, "F1", "build", "fast", "session", "sha", true, false, false, 0, 100, "");

        // If this were a plain string.Format, a malicious name would break the query.
        // Parameterised query treats the value as data, not SQL.
        var rows = _db.Query(
            "SELECT name FROM gates WHERE name = @name",
            ("@name", "build"));
        Assert.Single(rows);
    }

    [Fact]
    public void SeedCheckpoints_persists_and_re_seeds_idempotently()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        var cps = new (string, string, string, string, string, string)[]
        {
            ("F1.1", "F1", "run.db schema", "TODO", "-", "-"),
            ("F1.2", "F1", "tracker-as-view", "TODO", "-", "-"),
        };
        _db.SeedCheckpoints("r1", cps);

        var rows = _db.GetCheckpoints("r1");
        Assert.Equal(2, rows.Count);
        Assert.Equal("F1.1", rows[0].Id);
        Assert.Equal("F1", rows[0].StageId);
        Assert.Equal("run.db schema", rows[0].Title);
        Assert.Equal("TODO", rows[0].Status);

        // Re-seeding does not clobber status set by UpdateCheckpoint
        _db.UpdateCheckpoint("r1", "F1.1", "DONE", "abc123", "tests pass");
        _db.SeedCheckpoints("r1", cps); // re-seed
        var rows2 = _db.GetCheckpoints("r1");
        Assert.Equal("DONE", rows2[0].Status);
        Assert.Equal("abc123", rows2[0].Commit);
    }

    [Fact]
    public void UpdateCheckpoint_sets_status_commit_evidence()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.SeedCheckpoints("r1", [("F2.1", "F2", "process sup", "TODO", "-", "-")]);

        _db.UpdateCheckpoint("r1", "F2.1", "DONE", "def456", "12 tests, 0w/0e");
        var rows = _db.GetCheckpoints("r1");
        Assert.Single(rows);
        Assert.Equal("DONE", rows[0].Status);
        Assert.Equal("def456", rows[0].Commit);
        Assert.Equal("12 tests, 0w/0e", rows[0].Evidence);
    }

    [Fact]
    public void MarkCheckpointInProgress_transitions_from_todo_only()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.SeedCheckpoints("r1", [("F3.1", "F3", "stall v2", "TODO", "-", "-")]);

        _db.MarkCheckpointInProgress("r1", "F3.1");
        var rows = _db.GetCheckpoints("r1");
        Assert.Equal("IN PROGRESS", rows[0].Status);

        // Does not downgrade DONE
        _db.UpdateCheckpoint("r1", "F3.1", "DONE", "ghi", "ok");
        _db.MarkCheckpointInProgress("r1", "F3.1");
        var rows2 = _db.GetCheckpoints("r1");
        Assert.Equal("DONE", rows2[0].Status);
    }

    // ---------------------------------------------------------------- typed queries

    [Fact]
    public void QueryLedger_returns_entries()
    {
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.WriteLedger("r1", 1, "F1", "finding", "test note 1");
        _db.WriteLedger("r1", 1, "F1", "observation", "test note 2");

        var results = _db.QueryLedger("r1");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void QuerySessions_returns_summaries()
    {
        var started = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.InitializeStage("r1", "F1", "test");
        _db.RecordSession("r1", "F1", 1, "Deliver", started, null, null, "ses-1", 0, 1, "ok", null, 0, null);

        var results = _db.QuerySessions("r1");
        Assert.Single(results);
        Assert.Equal(1, results[0].Number);
    }

    // ---------------------------------------------------------------- F2.3: GetAllPids query

    [Fact]
    public void GetAllPids_returns_all_tracked_pids_for_run()
    {
        _db.InitializeRun("r-bg", "bg-plan", "/r", "b", "v");

        _db.TrackPid(10001, "r-bg", "bg:backtest", "F2", 1, DateTime.UtcNow);
        _db.TrackPid(10002, "r-bg", "bg:build", "F2", 1, DateTime.UtcNow);

        var pids = _db.GetAllPids("r-bg");
        Assert.Equal(2, pids.Count);
        Assert.Contains(pids, p => p.Pid == 10001 && p.Purpose == "bg:backtest");
        Assert.Contains(pids, p => p.Pid == 10002 && p.Purpose == "bg:build");
    }

    [Fact]
    public void GetAllPids_returns_empty_for_unknown_run()
    {
        _db.InitializeRun("r-bg2", "bg-plan", "/r", "b", "v");
        var pids = _db.GetAllPids("r-nonexistent");
        Assert.Empty(pids);
    }

    [Fact]
    public void GetAllPids_includes_exited_pids()
    {
        _db.InitializeRun("r-bg3", "bg-plan", "/r", "b", "v");

        _db.TrackPid(20001, "r-bg3", "bg:test", "F2", 2, DateTime.UtcNow);
        _db.MarkPidExited(20001, 0);

        var pids = _db.GetAllPids("r-bg3");
        Assert.Single(pids);
        Assert.Equal(20001, pids[0].Pid);
        Assert.NotNull(pids[0].ExitedUtc);
        Assert.Equal(0, pids[0].ExitCode);
    }

    // ---------------------------------------------------------------- run_state

    [Fact]
    public void SaveAndLoadRunState_round_trip()
    {
        _db.InitializeRun("r-state", "state-plan", "/r", "b", "v");
        _db.SaveRunState("r-state", "state-plan", "{\"planName\":\"state-plan\"}");

        var json = _db.LoadRunStateJson("r-state");
        Assert.Equal("{\"planName\":\"state-plan\"}", json);
    }
}
