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
