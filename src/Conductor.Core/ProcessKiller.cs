using System.Diagnostics;
using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>Run-scoped, ownership-validated process kill — the primitive behind <c>POST /processes/kill</c>
/// (the Face's Procs tab) and the same effect as <c>conductor bg stop &lt;pid&gt;</c>. It refuses any PID the
/// run didn't track, one that already exited, or the conductor process itself, so an operator (or a bug)
/// can never turn the Procs tab into "kill any process on the box". On success it kills the whole process
/// tree and marks the PID exited in run.db, so the liveness/stall views (which read run.db, not the live
/// <see cref="ProcessSupervisor"/> registry) reconcile immediately.</summary>
public static class ProcessKiller
{
    public readonly record struct Result(bool Ok, string? Error);

    public static Result Kill(IRunStore store, string runId, int pid)
    {
        if (pid == Environment.ProcessId) return new Result(false, "refusing to kill the conductor process itself");

        var tracked = store.GetAllPids(runId).FirstOrDefault(p => p.Pid == pid);
        if (tracked is null) return new Result(false, $"pid {pid} is not a tracked process of this run");
        if (tracked.ExitedUtc is not null) return new Result(false, $"pid {pid} has already exited");

        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch (ArgumentException) { /* process no longer exists — fall through and mark it exited */ }
        catch (InvalidOperationException) { /* already exited — same */ }

        store.MarkPidExited(pid, -1);
        return new Result(true, null);
    }
}
