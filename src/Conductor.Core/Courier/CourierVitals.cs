using System.Globalization;

namespace Conductor.Core.Courier;

/// <summary>What the presence record and the pid say together, in the four words a person reads.</summary>
public enum CourierLife
{
    /// <summary>No presence record: the last courier stopped cleanly, or none ever started.</summary>
    Absent,

    /// <summary>The pid is live and the loop came round recently (or it is still inside its first poll).</summary>
    Alive,

    /// <summary>The pid is live and the loop has not come round for longer than a healthy poll can take.</summary>
    Stale,

    /// <summary>A record nobody cleared, naming a process that is gone: a death nothing journaled.</summary>
    Dead,

    /// <summary>The pid is live but the record carries no heartbeat at all - a courier built before
    /// PK2.1. Not called alive, because nothing measures it; not called stale, because nothing says it
    /// stopped.</summary>
    Unmetered,
}

/// <summary>PK2.1 / D2(a) - the courier's liveness from its heartbeat, not from its pid alone.
///
/// <para>F-COUR-1: <c>running: yes</c> was decided by whether a pid existed, and <c>running: no</c>
/// said nothing about when it stopped. A courier that hangs in its loop looked healthy for as long as
/// the process stood, and a dead one left no time behind. The record now carries the last poll, so a
/// status says which of the four it is and, for a dead one, when it was last seen doing its job.</para></summary>
/// <param name="Life">Which of the four.</param>
/// <param name="Record">The presence record read, alive or not; null when <see cref="CourierLife.Absent"/>.</param>
/// <param name="LastSeenUtc">The last poll, or the start when no poll was recorded; null when absent.</param>
public sealed record CourierVitals(CourierLife Life, CourierPresence? Record, DateTimeOffset? LastSeenUtc)
{
    /// <summary>The longest one request to the messenger may hang before the transport gives up.</summary>
    internal const int RequestCeilingSeconds = 65;

    /// <summary>The longest the loop backs off a getUpdates conflict (<c>CourierDaemon.ConflictBackoff</c>).</summary>
    internal const int BackoffCeilingSeconds = 60;

    /// <summary>How long a healthy loop can go between beats, twice over: a request that hits the
    /// transport ceiling followed by the longer of the poll interval and the conflict backoff. Twice,
    /// so one slow iteration never reads as stale and two missed ones always do. With the default four
    /// second interval that is a little over four minutes.</summary>
    public static TimeSpan StaleAfter(int pollIntervalSeconds) =>
        TimeSpan.FromSeconds(2 * (RequestCeilingSeconds + Math.Max(BackoffCeilingSeconds, Math.Max(1, pollIntervalSeconds))));

    /// <summary>The vitals of the courier behind <paramref name="stateHomeRoot"/>'s presence record.</summary>
    /// <param name="probe">The start time of a pid, or null when no such process runs. For tests.</param>
    public static CourierVitals Read(TimeSpan staleAfter, DateTimeOffset nowUtc, string? stateHomeRoot = null,
        Func<int, DateTimeOffset?>? probe = null) =>
        Of(CourierPresence.Read(stateHomeRoot), CourierPresence.Live(stateHomeRoot, probe), staleAfter, nowUtc);

    /// <summary>The classification, from the record as written and the record only if its process is
    /// genuinely running (<see cref="CourierPresence.Live"/>).</summary>
    public static CourierVitals Of(CourierPresence? claimed, CourierPresence? live, TimeSpan staleAfter, DateTimeOffset nowUtc)
    {
        if (live is null && claimed is null) return new CourierVitals(CourierLife.Absent, null, null);

        var record = live ?? claimed!;
        var seen = record.LastPollUtc ?? record.StartedUtc;
        if (live is null) return new CourierVitals(CourierLife.Dead, record, seen);

        // No heartbeat yet is normal for the first poll; no heartbeat long after the start is a build
        // that never writes one.
        if (record.LastPollUtc is null && nowUtc - record.StartedUtc > staleAfter)
            return new CourierVitals(CourierLife.Unmetered, record, seen);

        return new CourierVitals(nowUtc - seen > staleAfter ? CourierLife.Stale : CourierLife.Alive, record, seen);
    }

    /// <summary>The word, lower case, as the status verb prints it and a script reads it.</summary>
    public string Word => Life.ToString().ToLowerInvariant();

    /// <summary>One line for a terminal: <c>alive</c>, <c>stale (last poll N min ago)</c>,
    /// <c>dead (last seen T)</c>, or why there is nothing to say.</summary>
    public string Describe(DateTimeOffset nowUtc)
    {
        var pid = Record is { } r ? "pid " + r.Pid.ToString(CultureInfo.InvariantCulture) : "";
        return Life switch
        {
            CourierLife.Absent => "absent (no presence record: the last courier stopped cleanly, or none has started)",
            CourierLife.Alive => Record!.LastPollUtc is { } polled
                ? $"alive (last poll {Ago(nowUtc - polled)}; {pid})"
                : $"alive (first poll in progress; {pid})",
            CourierLife.Stale => $"stale (last poll {Ago(nowUtc - LastSeenUtc!.Value)}; {pid} is still running)",
            CourierLife.Dead => $"dead (last seen {Stamp(LastSeenUtc!.Value)}, {Ago(nowUtc - LastSeenUtc.Value)}; "
                              + $"{pid} is gone and nothing cleared its record)",
            CourierLife.Unmetered => $"alive by pid only ({pid} writes no heartbeat - a courier built before PK2.1)",
            _ => Word,
        };
    }

    /// <summary>D2(b) - the line a new courier journals when it finds a record its predecessor did not
    /// clear: <c>previous courier pid N died silently; last poll T; task last-run result R</c>.</summary>
    /// <param name="previous">The record found at startup.</param>
    /// <param name="taskName">The task whose result was read, or null when there was none to read.</param>
    /// <param name="lastRun">What the scheduler said, or null when it could not be asked.</param>
    /// <param name="unread">Why the scheduler's answer is missing, when it is.</param>
    public static string DeathRecord(CourierPresence previous, string? taskName, CourierTaskRun? lastRun, string? unread = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var poll = previous.LastPollUtc is { } polled
            ? Stamp(polled)
            : "none recorded (started " + Stamp(previous.StartedUtc) + ")";
        var result = taskName is null
            ? "none (no task started it)"
            : lastRun is { } run
                ? run.Describe() + " [task " + taskName + "]"
                : "unread [task " + taskName + "]: " + (unread ?? "the scheduler did not say");
        return "previous courier pid " + previous.Pid.ToString(CultureInfo.InvariantCulture)
             + " died silently; last poll " + poll
             + "; task last-run result " + result;
    }

    private static string Stamp(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    internal static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 120) return ((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s ago";
        if (age.TotalMinutes < 120) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
        return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h "
             + age.Minutes.ToString(CultureInfo.InvariantCulture) + " min ago";
    }
}
