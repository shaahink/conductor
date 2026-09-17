using System.Globalization;

namespace Conductor.Core.Courier;

/// <summary>What one boundary check did about a courier that was not doing its job.</summary>
/// <param name="Started">Whether the scheduler accepted the start. Only a start counts as a restart.</param>
/// <param name="Found">What the heartbeat said: <see cref="CourierLife.Dead"/> or <see cref="CourierLife.Stale"/>.</param>
/// <param name="TaskName">The task it started, or tried to.</param>
/// <param name="Line">The sentence for the run log and the owner queue.</param>
public sealed record CourierRestartAttempt(bool Started, CourierLife Found, string TaskName, string Line);

/// <summary>PK2.2 / D2(e) - a live run is the courier's second supervisor.
///
/// <para>The scheduler's keep-alive trigger restarts a stopped courier within five minutes, but only if
/// the task is registered, enabled and the machine's scheduler is doing its job - and a run is awake
/// on this machine for hours at a time with a session boundary every few minutes. So at every boundary
/// the run reads the heartbeat, and a courier that is dead (a record nobody cleared) or stale (a live
/// pid whose loop stopped coming round) gets its task started by the run, which says so in its log and
/// on the owner's queue: a restart is a silent death, and the owner is the one who reads the cause.</para>
///
/// <para>A machine with no operative courier is untouched (<see cref="CourierPrecedence.Configured"/>),
/// and so is a courier that stopped cleanly: an absent record is somebody's <c>courier stop</c>, not
/// a death. What the scheduler remembers of the task's last run is read BEFORE the start, because the
/// start overwrites it with "running" (measured in PK2.1).</para></summary>
public sealed class CourierKeepAlive
{
    private readonly string? _stateHomeRoot;
    private readonly Func<int, DateTimeOffset?>? _probe;
    private readonly Func<string, CourierTask> _tasks;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    /// <param name="probe">Pid liveness (see <see cref="CourierPresence.Live"/>). For tests.</param>
    /// <param name="tasks">Builds the task to start, by name. Null for the real scheduler.</param>
    /// <param name="now">The clock. For tests.</param>
    public CourierKeepAlive(string? stateHomeRoot = null, Func<int, DateTimeOffset?>? probe = null,
        Func<string, CourierTask>? tasks = null, Func<DateTimeOffset>? now = null)
    {
        _stateHomeRoot = stateHomeRoot;
        _probe = probe;
        _tasks = tasks ?? (name => new CourierTask(name));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Checks the heartbeat and, when the courier is dead or stale, starts its task. Null when
    /// there was nothing to do.</summary>
    /// <param name="restartsSoFar">How many restarts this run has already made, for "Nth time".</param>
    public async Task<CourierRestartAttempt?> CheckAsync(int restartsSoFar)
    {
        if (!CourierPrecedence.Configured(_stateHomeRoot)) return null;

        var settings = CourierSettings.Load(_stateHomeRoot);
        var now = _now();
        var vitals = CourierVitals.Read(CourierVitals.StaleAfter(settings.PollIntervalSeconds), now, _stateHomeRoot, _probe);
        if (vitals.Life is not (CourierLife.Dead or CourierLife.Stale)) return null;

        var name = vitals.Record?.TaskName is { Length: > 0 } named ? named : CourierTask.DefaultName;
        var task = _tasks(name);
        var (lastRun, unread) = await task.LastRunAsync().ConfigureAwait(false);
        var found = "found " + vitals.Describe(now) + "; task \"" + name + "\" last run "
                  + (lastRun?.Describe() ?? "unread: " + unread);

        // IgnoreNew: while the task's hung instance runs, a start is silently ignored. End it first.
        var ended = "";
        if (vitals.Life == CourierLife.Stale)
        {
            var end = await task.StopAsync().ConfigureAwait(false);
            ended = end.Ok ? "; ended the hung instance" : "; could not end the hung instance: " + end.Complaint();
        }

        var start = await task.StartAsync().ConfigureAwait(false);
        return start.Ok
            ? new CourierRestartAttempt(true, vitals.Life, name,
                "courier restarted by this run, " + Ordinal(restartsSoFar + 1) + " time: " + found + ended)
            : new CourierRestartAttempt(false, vitals.Life, name,
                "courier not restarted: " + found + ended + "; this run could not start the task: " + start.Complaint());
    }

    /// <summary>The session-boundary form: check, log what was done, and count a restart on the run.
    /// Never fatal - a check that throws is a line in the log and the session starts anyway. True when
    /// <paramref name="state"/> changed and wants saving and reporting.</summary>
    public async Task<bool> AtBoundaryAsync(Models.RunState state, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);

        CourierRestartAttempt? attempt;
        try
        {
            attempt = await CheckAsync(state.CourierRestarts?.Count ?? 0).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            log("courier keep-alive check failed, session starts anyway: " + ex.Message);
            return false;
        }

        if (attempt is null) return false;
        log(attempt.Line);
        if (!attempt.Started) return false;

        state.CourierRestarts = new Models.CourierRestarts(
            (state.CourierRestarts?.Count ?? 0) + 1, _now().UtcDateTime, attempt.Line);
        return true;
    }

    /// <summary>1st, 2nd, 3rd, 4th ... 11th, 12th, 13th, 21st.</summary>
    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13
            ? "th"
            : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
