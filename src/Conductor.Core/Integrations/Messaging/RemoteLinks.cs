using System.Text.RegularExpressions;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — nothing in a push was ever a link, though <c>Reporter</c> has known how to build
/// remote URLs from a commit sha since the report existed. A sha in a chat is a string the owner has
/// to carry back to a machine; a link is one tap. The remote comes from
/// <see cref="Reporter.RemoteUrl"/>, so the report and the notifications cannot disagree about where
/// this repo lives, and a repo with no remote degrades to plain text rather than to a broken link.</summary>
public static partial class RemoteLinks
{
    /// <summary>A commit as <c>&lt;a href&gt;abc1234&lt;/a&gt;</c>, or the short sha when there is no
    /// remote. Accepts the "sha subject" form <c>Git.CommitsSince</c> returns as well as a bare sha.</summary>
    public static string Commit(string? remote, string shaOrLine)
    {
        ArgumentNullException.ThrowIfNull(shaOrLine);
        var sha = shaOrLine.Split(' ', 2)[0].Trim();
        if (sha.Length < 7 || !IsHex(sha)) return Escape(shaOrLine);
        var shortSha = sha[..7];
        return remote is { Length: > 0 }
            ? $"<a href=\"{Escape(remote)}/commit/{Escape(sha)}\">{shortSha}</a>"
            : shortSha;
    }

    /// <summary>The run's report where a phone can read it. The report is committed to
    /// <c>.conductor/REPORT.md</c> on the run's own branch (Reporter.WriteAndPublish), so the link is
    /// to that path on that branch — null when there is no remote to point at, in which case the
    /// composition drops the line rather than printing a dead one.</summary>
    public static string? Report(string? remote, string? branch) =>
        remote is { Length: > 0 } && branch is { Length: > 0 }
            ? $"{remote}/blob/{Uri.EscapeDataString(branch)}/.conductor/REPORT.md"
            : null;

    /// <summary>Turns <c>#123</c> into a pull-request link. The engine tracks no pull requests of its
    /// own — a PR reaches a push only because an agent wrote one into its result or handoff — so this
    /// is a rewrite over already-escaped text rather than a field.</summary>
    public static string LinkifyPullRequests(string escapedHtml, string? remote)
    {
        ArgumentNullException.ThrowIfNull(escapedHtml);
        if (remote is not { Length: > 0 }) return escapedHtml;
        return PrRef().Replace(escapedHtml, m =>
            $"<a href=\"{remote}/pull/{m.Groups["n"].Value}\">#{m.Groups["n"].Value}</a>");
    }

    /// <summary>A <c>#123</c> that is not part of a longer word and not already inside a URL. The
    /// leading boundary is explicit because <c>&amp;#39;</c> — an escaped apostrophe — would otherwise
    /// match, and turning an apostrophe into a pull-request link is exactly the kind of mangling that
    /// makes a rewrite over HTML a bad idea when it is not bounded.</summary>
    [GeneratedRegex(@"(?<![\w&/#])#(?<n>\d{1,7})\b", RegexOptions.None, 1000)]
    private static partial Regex PrRef();

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return false;
        return true;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal)
         .Replace(">", "&gt;", StringComparison.Ordinal);
}
