using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

/// <summary>
/// F2.1: Run-level process supervisor. Owns a Windows Job Object with KILL_ON_JOB_CLOSE so every
/// child (agent, gate, app-under-test) is guaranteed terminated when conductor exits or crashes.
/// Tracks every spawned PID in memory and in run.db for liveness queries (feeding the stall
/// detector in F2.4) and orphan reaping at startup.
/// No-op on non-Windows platforms (JobObject handles this internally); tracking still operates.
/// Singleton, registered in DI during ConductorHost.Build.
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private readonly JobObject _job = new();
    private readonly ConcurrentDictionary<int, TrackedProcess> _processes = new();
    private readonly ILogger<ProcessSupervisor> _logger;
    private readonly RunDb? _runDb;
    private readonly string? _runId;

    public ProcessSupervisor(ILogger<ProcessSupervisor> logger, string? runId = null, RunDb? runDb = null)
    {
        _logger = logger;
        _runDb = runDb;
        _runId = runId;
    }

    public IReadOnlyCollection<TrackedProcess> Processes =>
        _processes.Values.OrderBy(p => p.StartedUtc).ToList();

    /// <summary>Assign a process to the run-level JobObject and register it in the PID trackers.</summary>
    /// <returns>A disposable that, when disposed, marks the process as exited.</returns>
    public IDisposable Track(Process process, string purpose, string? stageId = null, int? sessionNumber = null)
    {
        var pid = process.Id;
        var tp = new TrackedProcess(pid, purpose, stageId, sessionNumber, DateTime.UtcNow);
        _processes[pid] = tp;

        _job.Assign(process);

        if (_runDb != null && _runId != null)
            _runDb.TrackPid(pid, _runId, purpose, stageId, sessionNumber, tp.StartedUtc);
        _logger.LogDebug("tracked pid={Pid} purpose={Purpose}", pid, purpose);

        return new TrackedProcessHandle(this, pid);
    }

    /// <summary>Mark a process as exited in the in-memory registry and run.db.</summary>
    public void UnTrack(int pid, int? exitCode = null)
    {
        if (_processes.TryRemove(pid, out _))
        {
            _runDb?.MarkPidExited(pid, exitCode);
            _logger.LogDebug("untracked pid={Pid} exitCode={ExitCode}", pid, exitCode);
        }
    }

    /// <summary>Find a tracked process by PID (null if not tracked or already exited).</summary>
    public TrackedProcess? Find(int pid) =>
        _processes.TryGetValue(pid, out var tp) ? tp : null;

    /// <summary>Reap orphans from a previous run that were never cleaned up. Queries run.db for
    /// unreaped PIDs belonging to the current run, kills any that are still alive, and marks them
    /// as exited. Also marks as exited any PIDs whose processes no longer exist (already terminated).</summary>
    public void ReapOrphans()
    {
        if (_runDb == null || _runId == null) return;

        var orphans = _runDb.GetOrphanPids(_runId);
        var currentPid = Environment.ProcessId;
        foreach (var (pid, purpose) in orphans)
        {
            if (pid == currentPid) continue;

            try
            {
                using var proc = Process.GetProcessById(pid);
                _logger.LogWarning("reaping orphan pid={Pid} purpose={Purpose}", pid, purpose);
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                proc.WaitForExit(3000);
            }
            catch (ArgumentException)
            {
                // Process no longer exists — just mark as exited
            }
            catch (InvalidOperationException)
            {
                // Process already exited
            }
            catch (NotSupportedException)
            {
                // Platform does not support this API
            }

            _runDb.MarkPidExited(pid, null);
        }
    }

    public void Dispose()
    {
        _job.Dispose();
        _processes.Clear();
    }

    private sealed class TrackedProcessHandle : IDisposable
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly int _pid;

        public TrackedProcessHandle(ProcessSupervisor supervisor, int pid)
        {
            _supervisor = supervisor;
            _pid = pid;
        }

        public void Dispose()
        {
            try { _supervisor.UnTrack(_pid); } catch { }
        }
    }
}

/// <summary>F2.1: A tracked process registered with the ProcessSupervisor.</summary>
public sealed record TrackedProcess(
    int Pid,
    string Purpose,
    string? StageId,
    int? SessionNumber,
    DateTime StartedUtc);
