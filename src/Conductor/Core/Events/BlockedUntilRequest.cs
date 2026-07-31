using System.Globalization;

namespace Conductor.Core.Events;

/// <summary>
/// SC5.1 — the one place "blocked until T because R" is parsed and judged, shared by the CLI verb
/// (<c>conductor task --blocked-until</c>), the MCP tool (<c>task_blocked_until</c>) and the engine
/// that honours the result. Keeping it in one place is the point: the CLI refuses a bad wait at the
/// moment the agent asks for it, and the run loop re-checks the SAME rules at verdict time, when the
/// session it came from has already been running for minutes.
/// </summary>
/// <remarks>
/// Why the ceiling exists: field notes 2026-07-29 (sk-platform #1) cost $51.98 because a session had
/// no way to say "wait". The answer to that must not be a session that can put the run to sleep
/// indefinitely — past a day, "I cannot proceed" is a human's decision, not a nap.
/// </remarks>
public static class BlockedUntilRequest
{
    /// <summary>The longest wait the engine will sit on. Beyond this the honest outcome is a park for
    /// a human, not a sleep nobody is watching.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    /// <summary>How many consecutive blocked sessions on one stage the engine will honour before it
    /// stops sleeping and asks for a human. Each block costs one session; three in a row means the
    /// agent's unblock estimate is not converging, which is exactly the loop this feature removed.</summary>
    public const int MaxConsecutiveBlocks = 3;

    /// <summary>Parse and judge an agent's wait request. Returns the resolved UTC instant, or an
    /// error string written for the agent that will read it. A timestamp with no offset is read as
    /// UTC — the resolved instant is always echoed back so an agent that meant local time sees it.</summary>
    public static (DateTimeOffset? UntilUtc, string? Error) Parse(
        string? isoTimestamp, string? reason, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(isoTimestamp))
            return (null, "a timestamp is required (ISO 8601, e.g. 2026-07-31T15:12:00Z)");

        // A wait with no reason is the exact knowledge loss this verb exists to stop: the next
        // session wakes up with no idea what it was waiting for and re-derives it, which is what
        // sk #1 paid $4.44 to do twice.
        if (string.IsNullOrWhiteSpace(reason))
            return (null, "a reason is required — say what the run is waiting for, so the session that wakes up does not re-derive it");

        if (!DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return (null, $"'{isoTimestamp}' is not an ISO 8601 timestamp (expected e.g. 2026-07-31T15:12:00Z)");

        var until = parsed.ToUniversalTime();
        if (until <= now)
            return (null, $"{until:yyyy-MM-dd HH:mm:ss}Z is not in the future (now {now:yyyy-MM-dd HH:mm:ss}Z) — there is nothing to wait for, carry on with the work");

        if (until - now > MaxWait)
            return (null, $"{until:yyyy-MM-dd HH:mm:ss}Z is {(until - now).TotalHours:0.#}h away, past the {MaxWait.TotalHours:0}h ceiling — a wait that long is a human's call: write a HUMAN: line in the handoff instead");

        return (until, null);
    }

    /// <summary>The wait as a surface renders it: "waiting until 15:12:00Z (2h13m) — reason".</summary>
    public static string Describe(DateTimeOffset untilUtc, string? reason, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var remaining = untilUtc - now;
        var window = remaining > TimeSpan.Zero
            ? $" ({FormatSpan(remaining)} from now)"
            : " (window already open)";
        var why = string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}";
        return $"waiting until {untilUtc:yyyy-MM-dd HH:mm:ss}Z{window}{why}";
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s" : $"{(int)span.TotalSeconds}s";
}
