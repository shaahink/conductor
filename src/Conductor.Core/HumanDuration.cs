namespace Conductor.Core;

/// <summary>
/// One elapsed-time renderer for everything an operator reads: <c>bg status</c>'s table, the MCP
/// task server's JSON, and anything added later.
/// </summary>
/// <remarks>
/// K2.1: this lived on <c>BgStatusHandler</c> in the CLI, and <c>McpTaskServer</c> - a core service with
/// no console at all - reached up into <c>Conductor.Commands</c> to borrow it. That was the ONE genuine
/// backwards reference the extraction found, and the fix is not a redirect: a duration format is domain
/// vocabulary, so it belongs below the CLI, where both callers can see it.
/// <para/>
/// Durations are computed in UTC by every caller. Mixing in a local-time value here is what once printed
/// <c>-1694s</c> for a live job - see <c>SqliteRunStore.ParseUtc</c>.
/// </remarks>
public static class HumanDuration
{
    /// <summary>Renders a span as <c>42s</c>, <c>7m 3s</c> or <c>2h 14m 9s</c> - largest unit first,
    /// never a bare total, because "8114s" is not something a human reads at a glance.</summary>
    public static string Format(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
    }
}
