using System.Diagnostics;
using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>W3.3: what the OS says about a pid we once tracked.</summary>
public enum PidState
{
    /// <summary>No process with that id exists any more.</summary>
    Gone,
    /// <summary>Alive, and its start time matches the one we recorded — it is still our process.</summary>
    Ours,
    /// <summary>Alive, but it started after we recorded ours: the id was recycled by the OS.</summary>
    Recycled,
    /// <summary>Alive, but its identity cannot be verified (start time unreadable).</summary>
    Unverifiable,
}

/// <summary>
/// W3.3: pid identity, in one place.
///
/// A pid is not a durable handle — operating systems recycle them, and conductor persists pids in
/// run.db across process restarts, so "is pid 12345 still running?" is genuinely ambiguous hours
/// later. <c>ReapOrphans</c> answered it with a bare existence check and then tree-killed whatever
/// it found; run.db has three stale unexited pids in it right now, any of which could by then
/// belong to something else entirely.
///
/// The start-time comparison settles it: a process we spawned was tracked within milliseconds of
/// starting, so anything that started meaningfully later under the same id is a different process.
/// </summary>
public static class PidLiveness
{
    /// <summary>How far a process's real start time may sit after the moment we recorded it before
    /// we call the id recycled. Tracking happens within milliseconds of the spawn; seconds of slack
    /// covers clock granularity and a loaded machine without covering a genuine reuse.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(10);

    public static PidState Check(int pid, DateTime trackedStartUtc)
    {
        if (pid <= 0) return PidState.Gone;
        try
        {
            using var proc = Process.GetProcessById(pid);
            bool exited;
            // SC4.1: HasExited opens a handle, and OpenProcess returns access-denied (Win32 5) for a
            // process this one may not touch — an elevated or protected owner of a RECYCLED id. That
            // throw escaped every caller: it took down `conductor bg status` outright, and it sits on
            // the path the battery settle and the stall rail both walk. Access denied is not "gone";
            // it is proof the process EXISTS and nothing more, which is exactly Unverifiable.
            try { exited = proc.HasExited; }
            catch (System.ComponentModel.Win32Exception) { return PidState.Unverifiable; }
            if (exited) return PidState.Gone;
            DateTime actualStartUtc;
            try { actualStartUtc = proc.StartTime.ToUniversalTime(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return PidState.Unverifiable;
            }
            return actualStartUtc > trackedStartUtc + StartTimeTolerance ? PidState.Recycled : PidState.Ours;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return PidState.Gone;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // GetProcessById itself was refused: the id is in use by something we cannot inspect.
            return PidState.Unverifiable;
        }
    }

    /// <summary>True only for a process that is still alive AND still the one we tracked.</summary>
    public static bool IsOurs(int pid, DateTime trackedStartUtc) => Check(pid, trackedStartUtc) == PidState.Ours;

    /// <summary>
    /// SC2.1: the liveness question every reporting surface asks — "is the thing we tracked still
    /// running?" — which is <see cref="IsOurs"/> widened by one case. A process whose start time cannot
    /// be read is alive and probably ours; <see cref="Sweep"/> already refuses to bury it, so the report
    /// must not either. Only <see cref="PidState.Gone"/> and <see cref="PidState.Recycled"/> mean dead.
    /// </summary>
    public static bool LooksAlive(int pid, DateTime trackedStartUtc) =>
        Check(pid, trackedStartUtc) is PidState.Ours or PidState.Unverifiable;

    /// <summary>
    /// Mark every tracked pid that the OS no longer knows — or that now belongs to someone else —
    /// as exited, so `bg status`, the Processes tab, and the stall rail stop reading a recycled id
    /// as live work. Best-effort: a store error is never fatal to the caller.
    /// </summary>
    public static int Sweep(IRunStore? store, string? runId)
    {
        if (store == null || string.IsNullOrEmpty(runId)) return 0;
        var swept = 0;
        try
        {
            foreach (var p in store.GetAllPids(runId))
            {
                if (p.ExitedUtc != null) continue;
                if (Check(p.Pid, p.StartedUtc) is PidState.Ours or PidState.Unverifiable) continue;
                try { store.MarkPidExited(p.Pid, null); swept++; } catch (InvalidOperationException) { }
            }
        }
        catch (InvalidOperationException) { }
        return swept;
    }
}
