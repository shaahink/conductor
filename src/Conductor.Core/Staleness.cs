using System.Globalization;

namespace Conductor.Core;

/// <summary>
/// SC2.2: one spelling for "how old is this failure". The engine's failure fields — <c>what hurt</c>,
/// <c>attentionReason</c> — are sticky by design (they must survive a restart), but with no age on them
/// a park raised four hours ago and one raised four seconds ago read identically on status, the report,
/// the Face and Telegram. Every surface that shows a sticky failure field renders its age through here,
/// so the vocabulary cannot drift between them.
/// </summary>
public static class Staleness
{
    /// <summary>Compact age: <c>3s</c>, <c>4m</c>, <c>2h 07m</c>, <c>3d 04h</c>. Clock skew (a stamp in
    /// the future) reads <c>0s</c> rather than a negative age.</summary>
    public static string Age(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0}s";
        if (span.TotalHours < 1) return $"{span.TotalMinutes:0}m";
        if (span.TotalDays < 1) return FormattableString.Invariant($"{(int)span.TotalHours}h {span.Minutes:00}m");
        return FormattableString.Invariant($"{(int)span.TotalDays}d {span.Hours:00}h");
    }

    /// <summary>The suffix a sticky field carries: age plus the wall-clock instant it happened, so a
    /// reader can line it up against the log without arithmetic. Returns "" for a null stamp (a
    /// state.json written before SC2.2) rather than inventing a time.</summary>
    public static string Since(DateTimeOffset? whenUtc, DateTimeOffset? nowUtc = null)
    {
        if (whenUtc is not { } w) return "";
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        return FormattableString.Invariant(
            $" [{Age(now - w)} ago, {w.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}Z]");
    }

    /// <inheritdoc cref="Since(DateTimeOffset?, DateTimeOffset?)"/>
    public static string Since(DateTime? whenUtc, DateTime? nowUtc = null)
        => Since(whenUtc is { } w ? new DateTimeOffset(DateTime.SpecifyKind(w, DateTimeKind.Utc)) : null,
                 nowUtc is { } n ? new DateTimeOffset(DateTime.SpecifyKind(n, DateTimeKind.Utc)) : null);
}
