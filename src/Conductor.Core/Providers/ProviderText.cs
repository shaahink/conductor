using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Providers;

internal static class ProviderText
{
    private static readonly Regex UsageLimitRx = new(
        @"usage limit|rate.?limit|overloaded|quota|out of credit|insufficient credit|credit balance|429|too many requests|5-hour|weekly limit",
        RegexOptions.IgnoreCase, ProgressConventions.RegexTimeout);

    // W3.2: an expired credential is not a rate limit and must never be treated as one. Waiting
    // 30 minutes and retrying cannot fix it, and the advisor is the same CLI, so its judgement dies
    // with the token. U-series session #13 carried every one of these phrases and was recorded as a
    // generic AgentError, which burned the remaining attempts.
    private static readonly Regex AuthFailureRx = new(
        @"authentication_failed|authentication error|failed to authenticate|oauth (session )?expired|" +
        @"invalid[ _-]?api[ _-]?key|invalid bearer|unauthorized|not authenticated|please (run |re-?)?login|" +
        // A bare 401 is a number ("build succeeded in 401 ms"); an HTTP 401 always arrives labelled,
        // as `"error_status":401`, `HTTP 401`, or `401 Unauthorized`.
        @"setup-token|(?:status|code|error|http)\W{0,3}401\b|\b401\s+unauthorized",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    /// <summary>DV2.4, bug #69 — a rate limit that says WHEN it lifts. Three shapes, all seen in the
    /// field: the Claude CLI's <c>Claude AI usage limit reached|1755567600</c> (a unix second), an
    /// HTTP <c>Retry-After: 3600</c> (seconds), and the English <c>try again in 4h 32m</c>.
    /// <para>Returns the wait, or null when the evidence names no reset — the caller then falls back
    /// to the plan's flat <c>backoffMinutes</c>. A reset already in the past answers null too: a stale
    /// timestamp must not turn into "wait zero" and a retry storm. Clamped to twelve hours, because a
    /// backend that says "come back in three days" is a decision for the owner, not a sleep.</para></summary>
    public static TimeSpan? ResetWait(string evidence, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(evidence)) return null;

        var epoch = EpochResetRx.Match(evidence);
        if (epoch.Success && long.TryParse(epoch.Groups["t"].Value, out var unix))
            return Clamp(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime - utcNow);

        var after = RetryAfterRx.Match(evidence);
        if (after.Success && int.TryParse(after.Groups["n"].Value, out var secs))
            return Clamp(TimeSpan.FromSeconds(secs));

        var words = TryAgainInRx.Match(evidence);
        if (words.Success)
        {
            var span = TimeSpan.FromHours(Num(words.Groups["h"])) + TimeSpan.FromMinutes(Num(words.Groups["m"]))
                     + TimeSpan.FromSeconds(Num(words.Groups["s"]));
            if (span > TimeSpan.Zero) return Clamp(span);
        }
        return null;

        static double Num(Group g) => g.Success && double.TryParse(g.Value,
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

        static TimeSpan? Clamp(TimeSpan wait)
            => wait <= TimeSpan.Zero ? null : wait > MaxResetWait ? MaxResetWait : wait;
    }

    /// <summary>The ceiling on a backend-supplied wait. Longer than any real limit window, short
    /// enough that a malformed timestamp cannot park a run for a week.</summary>
    public static readonly TimeSpan MaxResetWait = TimeSpan.FromHours(12);

    private static readonly Regex EpochResetRx = new(
        @"limit reached\|(?<t>\d{10,})", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    private static readonly Regex RetryAfterRx = new(
        @"retry[-_ ]?after\W{0,3}(?<n>\d{1,6})", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    private static readonly Regex TryAgainInRx = new(
        @"(?:try again in|resets? in|available again in)\s+(?:(?<h>\d{1,3})\s*h)?\s*(?:(?<m>\d{1,3})\s*m)?\s*(?:(?<s>\d{1,3})\s*s)?",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    public static bool DetectsUsageLimit(string evidence)
        => !string.IsNullOrEmpty(evidence) && UsageLimitRx.IsMatch(evidence);

    public static bool DetectsAuthFailure(string evidence)
        => !string.IsNullOrEmpty(evidence) && AuthFailureRx.IsMatch(evidence);

    public static string Trunc(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}
