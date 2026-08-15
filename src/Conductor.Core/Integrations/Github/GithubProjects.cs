using Conductor.Models;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.3 — the gate in front of a Projects v2 board, and, today, the whole of the project half.
///
/// <para><b>Why there is no GraphQL mutation in this file.</b> Projects v2 exists only in GitHub's
/// GraphQL API — REST cannot move a board item, so a REST attempt here would be a plausible-looking
/// no-op that passes a naive test. Writing one needs the classic <c>project</c> scope, and the
/// machine's token does not carry it (<c>gh auth status</c>: delete_repo, gist, read:org, repo,
/// user, workflow). Granting it is an interactive owner act. So the mutation path was NOT written:
/// it could not have been exercised even once, and this stage's contract makes half-done explicitly
/// worse than skipped — a board mirror that has never run against a real board is not a feature, it
/// is a claim. What IS here is the gate, which was exercised, and which refuses precisely.</para>
///
/// <para><b>Every branch refuses, and says which branch it is.</b> A caller asking for a project
/// board gets a named reason — bad config, missing scope, unreadable scopes, or "the mutation path
/// does not exist" — and never silence. The last of those is the one that keeps a later reader
/// honest: with the scope granted, this still refuses, and says so, rather than appearing to work.</para>
///
/// <para><b>Nothing here writes.</b> The scope check is a GET (<see cref="GithubClient.ProbeScopesAsync"/>).
/// The bar "zero mutations without the scope" is therefore structural, not a matter of ordering.</para>
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

    /// <summary>The standing fact, in one sentence, for the surfaces that state it without having
    /// probed anything — the live mirror says this at run start rather than making a network call on
    /// the startup path, and it is true whatever the token's scopes turn out to be.</summary>
    public const string NotImplementedLine = "the Projects v2 board is not implemented, so nothing was written.";

    /// <summary>
    /// Everything that must be true before a project board could be touched, checked in cost order:
    /// configuration first (free), then one GET for the token's scopes, then the standing fact that
    /// the mutation path is unbuilt. Returns the refusal to print, or an EMPTY list to proceed.
    ///
    /// <para>Empty is unreachable today and the last branch says why. It is the shape the success
    /// branch will need, not a promise that one exists.</para>
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

        if (!HasScope(rawScopes))
            return ScopeRefusal(rawScopes, tokenSource);

        return UnimplementedRefusal(rawScopes);
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

    /// <summary>
    /// The refusal for the branch nobody expects to hit: the scope IS present, and there is still no
    /// project board, because the mutation path was never built. It exists so that granting the scope
    /// produces a clear statement instead of a silent success — the failure this stage's cut line is
    /// aimed at is a reader concluding, from a passing gate, that a board is being mirrored.
    /// </summary>
    public static IReadOnlyList<string> UnimplementedRefusal(string? rawScopes) =>
    [
        NotImplementedLine,
        $"the '{RequiredScope}' scope IS present on this token ({string.Join(", ", Scopes(rawScopes))}), " +
        "so the gate is not what stopped this.",
        "KS9.3 was left SKIPPED rather than half-built: no GraphQL mutation path is merged, and there is " +
        "nothing here that could partly mirror a board.",
        $"the issue board (github.board '{GithubConfig.BoardIssues}') is unaffected and mirrors in full.",
    ];

    private static string Describe(string tokenSource) =>
        string.IsNullOrWhiteSpace(tokenSource) ? "(unknown)" : tokenSource;
}
