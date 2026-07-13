using System.Diagnostics;
using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>
/// F3.1: Multi-signal stall detector. A session is stalled ONLY when ALL signals are quiet:
/// (a) agent stdout/text output, (b) tool-call events, (c) liveness of supervised bg children.
/// "Quiet but its backtest is running" is NOT a stall — bg liveness keeps the session alive.
///
/// F3.2: Soft-kill debrief. On first stall detection, a grace window starts instead of an
/// immediate hard kill. During the grace window, the agent has one last chance to produce
/// output and recover. After the grace window expires, the session is hard-killed.
/// </summary>
public sealed class StallDetector
{
    private readonly TimeSpan _stallThreshold;
    private readonly TimeSpan _graceWindow;
    private readonly Func<DateTime> _clock;
    private DateTime? _firstDetectedAt;

    public StallDetector(TimeSpan stallThreshold, TimeSpan graceWindow, Func<DateTime>? clock = null)
    {
        _stallThreshold = stallThreshold;
        _graceWindow = graceWindow;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Reset detector state for a new session.</summary>
    public void Reset() => _firstDetectedAt = null;

    /// <summary>Whether the grace window is currently running.</summary>
    public bool InGraceWindow => _firstDetectedAt != null;

    /// <summary>
    /// Evaluate stall state against the three signals. Returns the current verdict.
    /// Call once per monitoring loop iteration.
    /// </summary>
    public StallVerdict Evaluate(
        DateTime lastActivityUtc,
        DateTime lastToolCallUtc,
        bool anyBgProcessAlive)
    {
        var now = _clock();

        // Any signal active → session is working, cancel any grace
        if ((now - lastActivityUtc) < _stallThreshold)
        {
            _firstDetectedAt = null;
            return StallVerdict.Active;
        }
        if (anyBgProcessAlive)
        {
            _firstDetectedAt = null;
            return StallVerdict.Active;
        }
        if ((now - lastToolCallUtc) < _stallThreshold)
        {
            _firstDetectedAt = null;
            return StallVerdict.Active;
        }

        // All signals quiet — stall detected. If no grace window, hard kill immediately.
        if (_graceWindow <= TimeSpan.Zero)
        {
            _firstDetectedAt = now;
            return StallVerdict.HardKill;
        }

        var graceElapsed = _firstDetectedAt.HasValue
            ? now - _firstDetectedAt.Value
            : TimeSpan.Zero;

        if (!_firstDetectedAt.HasValue)
        {
            _firstDetectedAt = now;
            return StallVerdict.SoftKillStarted;
        }

        if (graceElapsed >= _graceWindow)
            return StallVerdict.HardKill;

        return StallVerdict.GraceRunning;
    }

    /// <summary>
    /// Check whether any tracked bg process for the given run is still alive by
    /// inspecting the OS process table. Used by the liveness signal in the detector.
    /// Returns false if run.db is unavailable, the query fails, or no db is provided.
    /// </summary>
    public static bool AnyBgProcessAlive(IRunStore? store, string? runId)
    {
        if (store == null || runId == null) return false;
        try
        {
            var pids = store.GetAllPids(runId);
            foreach (var p in pids)
            {
                if (p.ExitedUtc != null) continue;
                try
                {
                    using var proc = Process.GetProcessById(p.Pid);
                    if (!proc.HasExited) return true;
                }
                catch
                {
                    // Process no longer exists (crashed/exited without run.db update);
                    // best-effort: mark it as exited so the next query is faster.
                    try { store.MarkPidExited(p.Pid, null); } catch { }
                }
            }
        }
        catch { }
        return false;
    }
}

/// <summary>F3.1: Verdict returned by <see cref="StallDetector.Evaluate"/>.</summary>
public enum StallVerdict
{
    Active,
    SoftKillStarted,
    GraceRunning,
    HardKill,
}
