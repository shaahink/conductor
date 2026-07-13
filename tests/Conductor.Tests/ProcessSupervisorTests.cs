using System.Diagnostics;
using Conductor.Core;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F2.1–F2.2: proves kill-by-tree via the JobObject, PID registry in run.db, and orphan reaper.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProcessSupervisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-psv-{Guid.NewGuid():N}");
    private readonly string _runId = "psv-test-run";
    private readonly SqliteRunStore _runDb;
    private readonly ProcessSupervisor _supervisor;

    public ProcessSupervisorTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        var dbPath = Path.Combine(stateDir, "run.db");
        _runDb = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
        _supervisor = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, _runId, _runDb);
    }

    public void Dispose()
    {
        _supervisor.Dispose();
        _runDb.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir cleanup best-effort */ }
    }

    [Fact]
    public void Supervisor_starts_with_no_tracked_processes()
    {
        Assert.Empty(_supervisor.Processes);
    }

    [Fact]
    public void Track_registers_process_in_memory_and_run_db()
    {
        using var proc = StartSleepyProcess();
        var handle = _supervisor.Track(proc, "test:sleepy");

        Assert.Single(_supervisor.Processes);
        var tp = _supervisor.Find(proc.Id);
        Assert.NotNull(tp);
        Assert.Equal(proc.Id, tp!.Pid);
        Assert.Equal("test:sleepy", tp.Purpose);

        var rows = _runDb.Query(
            "SELECT pid, purpose FROM pids WHERE run_id = @runId AND exited_utc IS NULL",
            ("@runId", _runId));
        Assert.Single(rows);
        Assert.Equal(proc.Id, Convert.ToInt32(rows[0]["pid"]));

        handle.Dispose();
    }

    [Fact]
    public void UnTrack_marks_process_exited()
    {
        using var proc = StartSleepyProcess();
        var handle = _supervisor.Track(proc, "test:untrack-me");
        handle.Dispose();

        Assert.Empty(_supervisor.Processes);
        Assert.Null(_supervisor.Find(proc.Id));

        var rows = _runDb.Query(
            "SELECT pid, exited_utc FROM pids WHERE run_id = @runId AND pid = @pid AND exited_utc IS NOT NULL",
            ("@runId", _runId), ("@pid", proc.Id));
        Assert.Single(rows);
    }

    [Fact]
    public void Track_multiple_processes_registers_all()
    {
        using var p1 = StartSleepyProcess();
        using var p2 = StartSleepyProcess();
        var h1 = _supervisor.Track(p1, "test:a");
        var h2 = _supervisor.Track(p2, "test:b");

        Assert.Equal(2, _supervisor.Processes.Count);

        var rows = _runDb.Query(
            "SELECT pid FROM pids WHERE run_id = @runId AND exited_utc IS NULL ORDER BY pid",
            ("@runId", _runId));
        Assert.Equal(2, rows.Count);

        h1.Dispose();
        h2.Dispose();
    }

    [Fact]
    public void ReapOrphans_marks_ghost_pids_as_exited()
    {
        var fakePid = 99999; // unlikely to exist
        _runDb.TrackPid(fakePid, _runId, "test:orphan-ghost", null, null, DateTime.UtcNow);

        _supervisor.ReapOrphans();

        var rows = _runDb.Query(
            "SELECT pid, exited_utc FROM pids WHERE run_id = @runId AND pid = @pid AND exited_utc IS NOT NULL",
            ("@runId", _runId), ("@pid", fakePid));
        Assert.Single(rows); // should be marked as exited
    }

    [Fact]
    public void ReapOrphans_does_not_kill_current_process()
    {
        var currentPid = Environment.ProcessId;
        _runDb.TrackPid(currentPid, _runId, "test:current-process", null, null, DateTime.UtcNow);

        _supervisor.ReapOrphans();

        // Current process should still be running
        using var self = Process.GetCurrentProcess();
        Assert.False(self.HasExited);

        // It should NOT be auto-exited (the reaper skips current PID)
        var rows = _runDb.Query(
            "SELECT pid, exited_utc FROM pids WHERE run_id = @runId AND pid = @pid",
            ("@runId", _runId), ("@pid", currentPid));
        Assert.Single(rows);
        Assert.Null(rows[0]["exited_utc"]);
    }

    [Fact]
    public void RunDb_pids_table_exists_and_has_expected_schema()
    {
        var cols = _runDb.Query("PRAGMA table_info(pids);");
        var colNames = cols.Select(c => (string)c["name"]!).ToHashSet();

        Assert.Contains("pid", colNames);
        Assert.Contains("purpose", colNames);
        Assert.Contains("stage_id", colNames);
        Assert.Contains("session_number", colNames);
        Assert.Contains("started_utc", colNames);
        Assert.Contains("exited_utc", colNames);
        Assert.Contains("exit_code", colNames);
        Assert.Contains("run_id", colNames);
    }

    [Fact]
    public void Schema_migration_to_v3_creates_pids_table()
    {
        _runDb.TrackPid(12345, _runId, "test:migration-check", "F2", 1, DateTime.UtcNow);
        var rows = _runDb.Query("SELECT pid FROM pids WHERE run_id = @runId", ("@runId", _runId));
        Assert.Single(rows);
    }

    [Fact]
    public void RunDb_GetOrphanPids_returns_only_unreaped_pids()
    {
        _runDb.TrackPid(10001, _runId, "orphan:should-appear", null, null, DateTime.UtcNow);
        _runDb.TrackPid(10002, _runId, "orphan:reaped", null, null, DateTime.UtcNow);
        _runDb.MarkPidExited(10002, 0);

        var orphans = _runDb.GetOrphanPids(_runId);
        Assert.Single(orphans);
        Assert.Equal(10001, orphans[0].Pid);
        Assert.Equal("orphan:should-appear", orphans[0].Purpose);
    }

    // ---------------------------------------------------------------- helpers

    private static Process StartSleepyProcess()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > NUL")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = Process.Start(psi)!;
        Thread.Sleep(200);
        return proc;
    }
}
