using Conductor.Models;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.3, moved by DV6.2 — the gate in front of a Projects v2 board. It is no longer the whole of
/// the project half: <see cref="GithubProjectSync"/> is.
///
/// <para><b>What this file used to say, and why it no longer says it.</b> KS9.3 left the mutation
/// path unwritten on purpose — it could not have been exercised even once, and half-done was
/// explicitly worse than skipped — so a token that DID carry the scope was still refused, with a
/// sentence saying the path did not exist. DV6.2 wrote the path, so that sentence would now be a
/// lie, and it is gone: with the scope granted this gate returns EMPTY and the board is written.
/// The refusal MOVED rather than being deleted, which is the distinction this checkpoint turns on.
/// </para>
///
/// <para><b>Every remaining branch refuses, and says which branch it is.</b> A caller asking for a
/// project board gets a named reason — bad config, missing scope, or unreadable scopes — and never
/// silence.</para>
///
/// <para><b>Nothing here writes.</b> The scope check is a GET (<see cref="GithubClient.ProbeScopesAsync"/>),
/// so the bar "zero mutations without the scope" is structural and not a matter of ordering.</para>
/// </summary>
public static class GithubProjects
{
    /// <summary>The classic OAuth scope a Projects v2 mutation needs. <c>read:project</c> is a
    /// different, weaker scope and is deliberately not accepted: this integration writes.</summary>
    public const string RequiredScope = "project";

    /// <summary>The one-time command that grants it. Named in the refusal, never run by conductor:
    /// it is interactive and it rewrites the machine's stored credential, which is an owner's
    /// decision and not a session's.</summary>
    public const string GrantCommand = "gh auth refresh -s project";

    /// <summary>DV6.2 — the standing fact for the surfaces that state it WITHOUT probing: the live
    /// mirror says this at run start rather than making a network call on the startup path, and it is
    /// true whatever the token's scopes turn out to be. It replaces KS9.3's "not implemented", which
    /// stopped being true the moment <see cref="GithubProjectSync"/> was merged.</summary>
    public const string NeedsScopeLine =
        "the Projects v2 board is attempted at each boundary; it needs the '" + RequiredScope +
        "' scope, and a pass whose token lacks it says so by name and leaves the issue board alone.";

    /// <summary>
    /// Everything that must be true before a project board is touched, checked in cost order:
    /// configuration first (free), then one GET for the token's scopes. Returns the refusal to print,
    /// or an EMPTY list to proceed.
    ///
    /// <para>DV6.2 — empty is REACHABLE now, and it means the board will be written. Under KS9.3 it
    /// was the shape a success branch would need; there is one.</para>
    /// </summary>
    /// <param name="config">The plan's github block. Callers that never asked for a project board
    /// should not call this at all — <see cref="GithubConfig.WantsProjectBoard"/> is that question.</param>
    /// <param name="tokenSource">Where the token came from, for a refusal that has to name it —
    /// <c>CONDUCTOR_GITHUB_TOKEN</c> or the secrets file's path.</param>
    public static async Task<IReadOnlyList<string>> PreflightAsync(
        GithubClient client, GithubConfig config, string tokenSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);

        var configRefusal = config.BoardRefusal();
        if (configRefusal is not null)
            return [configRefusal, "nothing was contacted and nothing was written."];

        var (rawScopes, error) = await client.ProbeScopesAsync(ct).ConfigureAwait(false);
        if (error is not null)
            return
            [
                $"could not read this token's scopes, so the project board was not attempted. nothing was written.",
                $"the check is a GET of /user: {error}",
                $"token source: {Describe(tokenSource)}",
            ];

        return HasScope(rawScopes) ? [] : ScopeRefusal(rawScopes, tokenSource);
    }

    /// <summary>True when GitHub's <c>X-OAuth-Scopes</c> header, verbatim, grants
    /// <see cref="RequiredScope"/>. A null header is false — a token whose scopes GitHub declines to
    /// state has not been shown to carry it, and this is the check that stands between a proof and a
    /// write.</summary>
    public static bool HasScope(string? rawScopes) =>
        Scopes(rawScopes).Contains(RequiredScope, StringComparer.OrdinalIgnoreCase);

    /// <summary>The header split into scope names, empty when GitHub reported none.</summary>
    public static IReadOnlyList<string> Scopes(string? rawScopes) =>
        string.IsNullOrWhiteSpace(rawScopes)
            ? []
            : [.. rawScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The refusal this stage was written to produce, as data so its four obligations are a test and
    /// not a reading: the scopes OBSERVED, the scope REQUIRED, WHERE the token came from, and the
    /// exact one-time command the owner runs.
    /// </summary>
    public static IReadOnlyList<string> ScopeRefusal(string? rawScopes, string tokenSource)
    {
        var observed = Scopes(rawScopes);
        var seen = observed.Count > 0
            ? string.Join(", ", observed)
            : rawScopes is null
                // No header at all. Saying "none" here would send an owner holding a working
                // fine-grained token off to run a command that cannot add a classic scope.
                ? "(none reported — GitHub sends X-OAuth-Scopes only for classic and OAuth tokens, " +
                  "so this is a fine-grained PAT or an app token)"
                : "(empty)";

        return
        [
            $"a Projects v2 board needs the '{RequiredScope}' scope and this token does not carry it. nothing was written.",
            $"scopes observed: {seen}",
            $"scope required: {RequiredScope} — Projects v2 is GraphQL-only, and the REST api cannot move a board item.",
            $"token source: {Describe(tokenSource)}",
            $"the owner grants it once, interactively: {GrantCommand}",
            "conductor will not run that: it is interactive and it rewrites this machine's stored credential.",
            $"until then set github.board to '{GithubConfig.BoardIssues}' — the issue board mirrors in full without it.",
        ];
    }

    private static string Describe(string tokenSource) =>
        string.IsNullOrWhiteSpace(tokenSource) ? "(unknown)" : tokenSource;
}
