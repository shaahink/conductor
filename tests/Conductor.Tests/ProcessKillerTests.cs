using System.Diagnostics;
using Conductor.Core;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>Proves ProcessKiller — the primitive behind POST /processes/kill (Face Procs tab) — kills a
/// tracked, live process and marks it exited, and refuses anything it must not touch (untracked pids,
/// the conductor process itself, already-exited pids). Spawns real child processes, hence Integration.</summary>
[Trait("Category", "Integration")]
public sealed class ProcessKillerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-kill-{Guid.NewGuid():N}");
    private const string RunId = "kill-run";
    private readonly SqliteRunStore _store;

    public ProcessKillerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Kill_TrackedLiveProcess_TerminatesItAndMarksExited()
    {
        using var proc = StartSleepyProcess();
        _store.TrackPid(proc.Id, RunId, "bg:test", "S1", 1, DateTime.UtcNow);

        var result = ProcessKiller.Kill(_store, RunId, proc.Id);

        Assert.True(result.Ok, result.Error);
        Assert.True(proc.WaitForExit(5000), "the process should have been killed");
        var row = Assert.Single(_store.GetAllPids(RunId), p => p.Pid == proc.Id);
        Assert.NotNull(row.ExitedUtc); // run.db reconciled so liveness/stall views agree
    }

    [Fact]
    public void Kill_UntrackedPid_RefusedAndLeavesProcessAlive()
    {
        using var proc = StartSleepyProcess(); // spawned but deliberately NOT tracked
        try
        {
            var result = ProcessKiller.Kill(_store, RunId, proc.Id);

            Assert.False(result.Ok);
            Assert.Contains("not a tracked process", result.Error);
            Assert.False(proc.HasExited, "an untracked process must never be killed");
        }
        finally { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
    }

    [Fact]
    public void Kill_ConductorsOwnPid_Refused()
    {
        var result = ProcessKiller.Kill(_store, RunId, Environment.ProcessId);

        Assert.False(result.Ok);
        Assert.Contains("conductor process itself", result.Error);
    }

    [Fact]
    public void Kill_AlreadyExitedPid_Refused()
    {
        _store.TrackPid(4242, RunId, "bg:test", null, null, DateTime.UtcNow);
        _store.MarkPidExited(4242, 0);

        var result = ProcessKiller.Kill(_store, RunId, 4242);

        Assert.False(result.Ok);
        Assert.Contains("already exited", result.Error);
    }

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
