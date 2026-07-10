using System.Diagnostics;
using Conductor.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F2.4 harness proofs: kill-by-tree, orphan reap end-to-end, bg liveness feeds stall detector.
/// These are integration-level tests that prove the process supervision primitives work as
/// intended before they feed the F3 stall detector.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProcessSupervisorHarnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-f24-{Guid.NewGuid():N}");
    private readonly string _runId = "f24-harness-run";
    private readonly RunDb _runDb;
    private readonly ProcessSupervisor _supervisor;

    public ProcessSupervisorHarnessTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        var dbPath = Path.Combine(stateDir, "run.db");
        _runDb = new RunDb(dbPath, NullLogger<RunDb>.Instance);
        _supervisor = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, _runId, _runDb);
    }

    public void Dispose()
    {
        _supervisor.Dispose();
        _runDb.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    // ---------------------------------------------------------------- kill-by-tree

    /// <summary>
    /// F2.4: proves that the JobObject's KILL_ON_JOB_CLOSE terminates all assigned processes
    /// when the job handle closes (simulating conductor crash or supervisor disposal).
    /// This is the mechanism ProcessSupervisor uses to guarantee no orphans across crashes.
    /// </summary>
    [Fact]
    public void JobObject_KillOnClose_TerminatesAssignedProcesses()
    {
        using var p1 = StartSleepyProcess();
        using var p2 = StartSleepyProcess();

        Assert.True(IsProcessAlive(p1.Id), "p1 should be alive before JobObject disposal.");
        Assert.True(IsProcessAlive(p2.Id), "p2 should be alive before JobObject disposal.");

        // Assign both to a fresh JobObject (not the supervisor's — to test the primitive directly)
        using (var job = new JobObject())
        {
            job.Assign(p1);
            job.Assign(p2);
        } // Dispose → KILL_ON_JOB_CLOSE fires

        // Give the OS a moment to terminate
        Thread.Sleep(1000);

        Assert.False(IsProcessAlive(p1.Id), "p1 should be dead after JobObject disposal.");
        Assert.False(IsProcessAlive(p2.Id), "p2 should be dead after JobObject disposal.");
    }

    /// <summary>
    /// F2.4: proves that ProcessSupervisor.Track + dispose kills tracked processes via
    /// the JobObject (KILL_ON_JOB_CLOSE). Uses a dedicated supervisor instance to avoid
    /// interfering with other tests sharing the class-level instance.
    /// </summary>
    [Fact]
    public void Supervisor_Dispose_KillsAllTrackedProcesses()
    {
        var dbPath = Path.Combine(_dir, ".conductor", $"sv-dispose-{Guid.NewGuid():N}.db");
        using var localDb = new RunDb(dbPath, NullLogger<RunDb>.Instance);
        using var sv = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, _runId, localDb);

        using var proc = StartSleepyProcess();
        sv.Track(proc, "test:supervisor-kill");

        Assert.True(IsProcessAlive(proc.Id), "Should be alive after Track.");
        Assert.Single(sv.Processes);

        sv.Dispose(); // JobObject → KILL_ON_JOB_CLOSE

        Thread.Sleep(1000);
        Assert.False(IsProcessAlive(proc.Id), "Process should be dead after supervisor disposal.");
    }

    /// <summary>
    /// F2.4: proves that tracked PIDs surviving a superviser disposal (simulating conductor
    /// crash) are reaped by the orphan reaper on the next startup.
    /// </summary>
    [Fact]
    public void OrphanReap_MarksAbandonedPidAsExited()
    {
        using var proc = StartSleepyProcess();
        _supervisor.Track(proc, "test:will-be-orphan");
        proc.Kill();
        proc.WaitForExit(3000);

        // The PID is still tracked in run.db as not-exited (we didn't call UnTrack)
        var orphans1 = _runDb.GetOrphanPids(_runId);
        Assert.Single(orphans1);
        Assert.Equal(proc.Id, orphans1[0].Pid);

        // Simulate a fresh startup: new ProcessSupervisor for the same run reaps orphans
        using var supervisor2 = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, _runId, _runDb);
        supervisor2.ReapOrphans();

        // The orphan PID should be marked as exited in run.db
        var orphans2 = _runDb.GetOrphanPids(_runId);
        Assert.Empty(orphans2);

        // Verify it's indeed marked as exited
        var rows = _runDb.Query(
            "SELECT pid, exited_utc FROM pids WHERE run_id = @runId AND pid = @pid",
            ("@runId", _runId), ("@pid", proc.Id));
        Assert.Single(rows);
        Assert.NotNull(rows[0]["exited_utc"]);
    }

    /// <summary>
    /// F2.4: proves that run.db pids table can feed a liveness monitor (the stall detector
    /// in F3). Queries all tracked PIDs, checks each against the OS, and produces a liveness
    /// summary — the exact data pipeline the stall detector will consume.
    /// </summary>
    [Fact]
    public void LivenessFeed_RunningAndDeadProcesses_ReportedCorrectly()
    {
        // Track 2 processes: one alive, one fake/dead
        using var aliveProc = StartSleepyProcess();
        _supervisor.Track(aliveProc, "test:alive-for-liveness");
        _runDb.TrackPid(99998, _runId, "test:dead-fake", null, null, DateTime.UtcNow);

        // Query the pids table and produce a liveness report (what F3 will do)
        var pids = _runDb.GetAllPids(_runId);
        Assert.Equal(2, pids.Count);

        var liveness = pids.Select(p =>
        {
            var alive = IsProcessAlive(p.Pid);
            return new { p.Pid, p.Purpose, Alive = alive };
        }).ToList();

        var aliveEntry = liveness.First(l => l.Pid == aliveProc.Id);
        Assert.True(aliveEntry.Alive, $"PID {aliveProc.Id} ('{aliveEntry.Purpose}') should be alive.");

        var deadEntry = liveness.First(l => l.Pid == 99998);
        Assert.False(deadEntry.Alive, $"PID 99998 ('{deadEntry.Purpose}') should be dead.");

        // Clean up: kill the alive process and verify it becomes dead
        aliveProc.Kill();
        aliveProc.WaitForExit(3000);

        var postKill = IsProcessAlive(aliveProc.Id);
        Assert.False(postKill, $"PID {aliveProc.Id} should be dead after kill.");
    }

    /// <summary>
    /// F2.4: proves that GetAllPids returns zero rows when no processes are tracked
    /// for a given run — the stall detector sees an empty feed, not a crash.
    /// </summary>
    [Fact]
    public void LivenessFeed_EmptyRun_ReturnsEmptyList()
    {
        var pids = _runDb.GetAllPids("nonexistent-run-id");
        Assert.Empty(pids);
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

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }
}
