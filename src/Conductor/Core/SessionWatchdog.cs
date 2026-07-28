using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Conductor.Core;

/// <summary>W3.1: what the watchdog decided on one tick.</summary>
public enum WatchdogAction
{
    /// <summary>Nothing to report (or a diagnostic carried in the message, e.g. a clock jump).</summary>
    None,
    Diagnostic,
    StallGraceStarted,
    StallGraceRunning,
    StallKill,
    Timeout,
}

/// <summary>W3.1: the three liveness signals, sampled once per watchdog tick.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct WatchdogSignals(
    DateTime LastActivityUtc,
    DateTime LastToolCallUtc,
    bool AnyBgProcessAlive);

/// <summary>
/// W3.1: the session's autonomy rails, on a thread of their own.
///
/// The hard timeout used to be a wall-clock comparison inside the agent poll loop
/// (<c>SessionRunner</c>) — so it could only fire when the loop got around to it. Bug #8: a
/// U-series session hung at ~2 minutes and the 90-minute timeout fired at **337 minutes**. This
/// watchdog runs on a dedicated background thread, so the kill is never gated on the loop making
/// progress, on the thread pool, or on any await completing.
///
/// It also reconciles two clocks. Monotonic time (<see cref="Stopwatch"/>) does not advance while
/// the machine sleeps or hibernates; wall-clock time does. Their divergence on a tick IS the
/// suspend, and it is excluded from both budgets: a laptop that slept for four hours must not come
/// back to a session killed for "stalling" the whole time, nor keep running one that has already
/// burned its timeout. A backwards wall-clock step (NTP correction, manual change) is reported and
/// ignored — the monotonic clock is the authority.
/// </summary>
public sealed class SessionWatchdog : IDisposable
{
    /// <summary>A wall-vs-monotonic divergence at or beyond this on a single tick is a clock jump,
    /// not measurement noise.</summary>
    public static readonly TimeSpan DefaultClockJumpTolerance = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _hardTimeout;
    private readonly StallDetector _stall;
    private readonly Func<WatchdogSignals> _sample;
    private readonly Action<WatchdogAction, string> _onAction;
    private readonly Func<TimeSpan> _monotonic;
    private readonly Func<DateTime> _wallClock;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _jumpTolerance;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Lock _gate = new();

    private readonly TimeSpan _startMono;
    private readonly DateTime _startWall;
    private TimeSpan _lastMono;
    private DateTime _lastWall;
    private TimeSpan _suspended;
    private bool _graceReported;
    private volatile bool _stalled;
    private volatile bool _timedOut;
    private Thread? _thread;

    public SessionWatchdog(
        TimeSpan hardTimeout,
        TimeSpan stallThreshold,
        TimeSpan stallGrace,
        Func<WatchdogSignals> sample,
        Action<WatchdogAction, string> onAction,
        TimeSpan? tickInterval = null,
        Func<TimeSpan>? monotonic = null,
        Func<DateTime>? wallClock = null,
        TimeSpan? clockJumpTolerance = null)
    {
        _hardTimeout = hardTimeout;
        _sample = sample;
        _onAction = onAction;
        _tick = tickInterval ?? TimeSpan.FromSeconds(1);
        _jumpTolerance = clockJumpTolerance ?? DefaultClockJumpTolerance;
        var sw = Stopwatch.StartNew();
        _monotonic = monotonic ?? (() => sw.Elapsed);
        _wallClock = wallClock ?? (() => DateTime.UtcNow);
        _startMono = _lastMono = _monotonic();
        _startWall = _lastWall = _wallClock();
        // The stall budget is measured on the same suspend-corrected clock as the timeout, so a
        // sleeping machine cannot manufacture a stall out of an agent that was simply frozen with it.
        _stall = new StallDetector(stallThreshold, stallGrace, () => _wallClock() - _suspended);
    }

    /// <summary>True once the stall rail has killed the session.</summary>
    public bool Stalled => _stalled;

    /// <summary>True once the hard timeout has killed the session.</summary>
    public bool TimedOut => _timedOut;

    /// <summary>Time the machine spent suspended during this session, excluded from both budgets.</summary>
    public TimeSpan ExcludedSuspendTime { get { lock (_gate) return _suspended; } }

    /// <summary>Session time that actually counted: the more conservative of the monotonic reading
    /// and the suspend-corrected wall reading, so neither a stopped nor a running QPC-during-sleep
    /// implementation can inflate it.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                var mono = _monotonic() - _startMono;
                var wall = _wallClock() - _startWall - _suspended;
                return mono < wall ? mono : wall;
            }
        }
    }

    /// <summary>Start the independent watchdog thread. Call at most once.</summary>
    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "conductor-session-watchdog",
        };
        _thread.Start();
    }

    /// <summary>Stop the thread and wait briefly for it to unwind.</summary>
    public void Stop()
    {
        _stop.Set();
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch (ThreadStateException) { }
        _thread = null;
    }

    /// <summary>
    /// One evaluation. Pure with respect to the injected clocks — tests drive it directly instead
    /// of waiting on a real thread. Returns the action the caller must act on; every action carries
    /// a human-readable message for the run log.
    /// </summary>
    public (WatchdogAction Action, string Message) Tick()
    {
        lock (_gate)
        {
            var mono = _monotonic();
            var wall = _wallClock();
            var skew = (wall - _lastWall) - (mono - _lastMono);
            _lastMono = mono;
            _lastWall = wall;

            if (skew >= _jumpTolerance)
            {
                _suspended += skew;
                return (WatchdogAction.Diagnostic,
                    $"clock jump: wall clock ran {Fmt(skew)} ahead of the monotonic clock (machine slept?) — " +
                    $"excluded from the timeout and stall budgets ({Fmt(_suspended)} excluded so far)");
            }
            if (skew <= -_jumpTolerance)
            {
                return (WatchdogAction.Diagnostic,
                    $"clock jump: wall clock stepped {Fmt(-skew)} BACKWARDS (NTP correction?) — " +
                    "ignoring it; the monotonic clock governs this session");
            }

            if (!_timedOut)
            {
                var elapsedMono = mono - _startMono;
                var elapsedWall = wall - _startWall - _suspended;
                var elapsed = elapsedMono < elapsedWall ? elapsedMono : elapsedWall;
                if (elapsed >= _hardTimeout)
                {
                    _timedOut = true;
                    var suspendNote = _suspended > TimeSpan.Zero ? $" ({Fmt(_suspended)} of machine sleep excluded)" : "";
                    return (WatchdogAction.Timeout,
                        $"timeout: session exceeded {Fmt(_hardTimeout)}{suspendNote} — killing");
                }
            }

            if (_stalled) return (WatchdogAction.None, "");

            var signals = _sample();
            var verdict = _stall.Evaluate(signals.LastActivityUtc, signals.LastToolCallUtc, signals.AnyBgProcessAlive);
            switch (verdict)
            {
                case StallVerdict.Active:
                    _graceReported = false;
                    return (WatchdogAction.None, "");
                case StallVerdict.SoftKillStarted:
                    _graceReported = true;
                    return (WatchdogAction.StallGraceStarted,
                        $"stall: all signals quiet for {Fmt(_stall.Threshold)} — {Fmt(_stall.Grace)} soft-kill grace window started");
                case StallVerdict.GraceRunning:
                    if (_graceReported) return (WatchdogAction.None, "");
                    _graceReported = true;
                    return (WatchdogAction.StallGraceRunning, "stall: in soft-kill grace window — waiting for agent to recover");
                default:
                    _stalled = true;
                    return (WatchdogAction.StallKill, "stall: grace window expired — killing session");
            }
        }
    }

    private void Run()
    {
        // Wait first: a watchdog that fires on tick zero would race the agent's own startup.
        while (!_stop.Wait(_tick))
        {
            try
            {
                var (action, message) = Tick();
                if (action == WatchdogAction.None) continue;
                _onAction(action, message);
                // The kill is one-shot; the poll loop owns everything after the process dies.
                if (action is WatchdogAction.StallKill or WatchdogAction.Timeout) return;
            }
            catch (Exception ex)
            {
                // A watchdog that dies silently is bug #8 again. Report and keep ticking.
                try { _onAction(WatchdogAction.Diagnostic, $"watchdog tick failed: {ex.Message}"); } catch { }
            }
        }
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#}m" : $"{t.TotalSeconds:0.#}s";

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}
