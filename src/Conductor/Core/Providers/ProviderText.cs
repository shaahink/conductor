using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Providers;

internal static class ProviderText
{
    private static readonly Regex UsageLimitRx = new(
        @"usage limit|rate.?limit|overloaded|quota|out of credit|insufficient credit|credit balance|429|too many requests|5-hour|weekly limit",
        RegexOptions.IgnoreCase, ProgressConventions.RegexTimeout);

    public static bool DetectsUsageLimit(string evidence)
        => !string.IsNullOrEmpty(evidence) && UsageLimitRx.IsMatch(evidence);

    public static string Trunc(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}
