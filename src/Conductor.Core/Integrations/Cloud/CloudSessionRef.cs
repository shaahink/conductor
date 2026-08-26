using System.Text.RegularExpressions;

namespace Conductor.Core.Integrations.Cloud;

/// <summary>DV5.1 — a cloud session, as the owner can name it from a phone.
///
/// <para><c>claude --help</c> says <c>--cloud</c> attaches "by session ID or claude.ai/code URL", so
/// both are accepted and whichever one the owner gave is what is echoed back. The engine never
/// SYNTHESISES a URL from an id: this session has never observed the URL shape of a real cloud
/// session, and a link that 404s is worse from a phone than no link at all. When only an id is
/// known the reply names <see cref="CloudCliFacts.SessionHome"/> instead.</para></summary>
/// <param name="Id">What is passed to <c>--cloud</c>.</param>
/// <param name="Url">The URL the owner gave, or one observed in the CLI's own output. Never invented.</param>
public sealed partial record CloudSessionRef(string Id, string? Url)
{
    [GeneratedRegex(@"^https?://(?:www\.)?claude\.ai/code/(?<id>[A-Za-z0-9_-]{6,})(?:[/?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 500)]
    private static partial Regex UrlShape();

    [GeneratedRegex(@"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|sess_[A-Za-z0-9_-]{6,})$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 500)]
    private static partial Regex IdShape();

    /// <summary>Finds a claude.ai/code link anywhere in CLI output. Used to UPGRADE a bare id to a
    /// link the owner can tap — never to guess one.</summary>
    [GeneratedRegex(@"https?://(?:www\.)?claude\.ai/code/[A-Za-z0-9_/?=&#-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 500)]
    public static partial Regex LinkInText();

    /// <summary>Reads the first token of a <c>/cloud</c> argument as a session reference, or answers
    /// null so the caller treats the whole argument as a task description.
    ///
    /// <para>Strict on purpose. A loose match would swallow the first word of "refactor the courier"
    /// and send it to a session that does not exist; the cost of being strict is that an unusual id
    /// shape reads as a task, and the create path then names exactly what it saw.</para></summary>
    public static CloudSessionRef? TryParse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var t = token.Trim();
        if (t.Length == 0) return null;

        var url = UrlShape().Match(t);
        if (url.Success) return new CloudSessionRef(url.Groups["id"].Value, t);

        return IdShape().IsMatch(t) ? new CloudSessionRef(t, null) : null;
    }

    /// <summary>The same reference with a URL observed in CLI output, when it did not have one.</summary>
    public CloudSessionRef WithUrlFrom(string? output)
    {
        if (Url is not null || string.IsNullOrEmpty(output)) return this;
        var hit = LinkInText().Match(output);
        return hit.Success ? this with { Url = hit.Value } : this;
    }

    /// <summary>How the session is named back to the chat: a tappable link when one is known, the id
    /// plus where to find it when one is not.</summary>
    public string Describe() => Url is { Length: > 0 } u ? $"{Id} — {u}" : $"{Id} (find it at {CloudCliFacts.SessionHome})";
}
