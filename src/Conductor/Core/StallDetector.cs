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

    /// <summary>Quiet-on-all-signals duration that starts the grace window.</summary>
    public TimeSpan Threshold => _stallThreshold;

    /// <summary>Recovery window between first detection and the hard kill.</summary>
    public TimeSpan Grace => _graceWindow;

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

    /// <summary>W3.1: only a deliberately backgrounded child (<c>conductor bg start</c>, the MCP
    /// <c>bg_start</c> tool) is "work happening while the agent is quiet". Everything else the
    /// supervisor tracks — the agent process itself, the Face, gate commands — is either the thing
    /// we are judging or an unrelated bystander.</summary>
    public const string BgPurposePrefix = "bg:";

    /// <summary>
    /// Check whether any tracked <c>bg:*</c> process for the given run is still alive by
    /// inspecting the OS process table. Used by the liveness signal in the detector.
    /// Returns false if run.db is unavailable, the query fails, or no db is provided.
    ///
    /// W3.1: the purpose filter is why this detector was dead code. Every session tracks the
    /// agent's own pid (<c>agent:stage:…</c>) and most runs track the Face (<c>face:tui</c>) —
    /// both always alive, so "a bg process is alive" was permanently true and no engine log ever
    /// written contained a single <c>stall:</c> line.
    /// </summary>
    public static bool AnyBgProcessAlive(IRunStore? store, string? runId)
    {
        if (store == null || runId == null) return false;
        var alive = false;
        try
        {
            var pids = store.GetAllPids(runId);
            foreach (var p in pids)
            {
                if (p.ExitedUtc != null) continue;
                var isBg = p.Purpose.StartsWith(BgPurposePrefix, StringComparison.OrdinalIgnoreCase);
                // W3.3: "alive" means still OUR process, not merely "some process holds that id".
                switch (PidLiveness.Check(p.Pid, p.StartedUtc))
                {
                    case PidState.Ours or PidState.Unverifiable:
                        if (isBg) alive = true;
                        break;
                    default:
                        // Gone, or the id was recycled by something else: mark it exited so the next
                        // query is faster. This sweep still covers EVERY purpose — the bg filter
                        // changes what counts as liveness, not what gets cleaned up.
                        try { store.MarkPidExited(p.Pid, null); } catch { }
                        break;
                }
            }
        }
        catch { }
        return alive;
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
