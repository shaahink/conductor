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
            if (proc.HasExited) return PidState.Gone;
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
    }

    /// <summary>True only for a process that is still alive AND still the one we tracked.</summary>
    public static bool IsOurs(int pid, DateTime trackedStartUtc) => Check(pid, trackedStartUtc) == PidState.Ours;

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
