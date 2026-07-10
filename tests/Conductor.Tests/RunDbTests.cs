using System.Data;
using Conductor.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F1.1: Proves the run.db schema auto-creates on open, write methods persist data,
/// and the Query surface returns correct results for ad-hoc reporting (F1.4 foundation).
/// </summary>
public sealed class RunDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RunDb _db;

    public RunDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conductor-rundb-test-{Guid.NewGuid():N}.db");
        var logger = NullLogger<RunDb>.Instance;
        _db = new RunDb(_dbPath, logger);
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
    }

    [Fact]
    public void Schema_version_is_one()
    {
        var rows = _db.Query("SELECT version FROM schema_version");
        Assert.Single(rows);
        Assert.Equal(1L, (long)rows[0]["version"]!);
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
    public void Attempt_record_round_trip()
    {
        var started = new DateTime(2026, 7, 10, 13, 0, 0, DateTimeKind.Utc);
        _db.InitializeRun("r1", "p", "/r", "b", "v");
        _db.RecordAttempt("r1", "F1", 1, 1, started);

        var rows = _db.Query("SELECT * FROM attempts WHERE run_id = 'r1'");
        Assert.Single(rows);
        Assert.Equal(1L, rows[0]["number"]);
        Assert.Equal(1L, rows[0]["session_number"]);
    }

    [Fact]
    public void Idempotent_schema_creation()
    {
        // Opening a second RunDb on the same file should not throw or duplicate tables
        var logger = NullLogger<RunDb>.Instance;
        using var db2 = new RunDb(_dbPath, logger);

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
}
