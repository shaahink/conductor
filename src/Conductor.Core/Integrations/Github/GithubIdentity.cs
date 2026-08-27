using System.Globalization;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.1 — the two identity questions the mirror asks: WHICH repository, and WHICH issue is already
/// ours.
///
/// <para><b>The marker is the identity, not the title.</b> An issue's title is the checkpoint's
/// title, and a checkpoint's title changes when the plan is edited. Matching on it would mint a
/// second issue the first time a card was reworded — the exact duplicate the idempotence bar
/// forbids. So every issue conductor creates carries an HTML comment naming the task id, invisible
/// in GitHub's rendering, and that comment is what a later pass matches on.</para>
///
/// <para><b>Owner/repo is derived, not re-shelled.</b> <c>Reporter.RemoteUrl</c> already normalises
/// <c>git@</c> and <c>https</c> origins and caches the answer under a lock; a second implementation
/// here would be a second thing to get wrong AND a second <c>git remote get-url</c> per call. The
/// normalisation itself moved down here (<see cref="NormaliseRemoteUrl"/>) so both callers share one
/// definition of what a remote URL means.</para>
/// </summary>
public static class GithubIdentity
{
    /// <summary>Environment variable holding the token. Wins over the secrets file, exactly as
    /// <c>CONDUCTOR_TELEGRAM_TOKEN</c> does for the bot.</summary>
    public const string TokenEnvVar = "CONDUCTOR_GITHUB_TOKEN";

    /// <summary>The stable per-task marker planted in an issue body.</summary>
    public static string TaskMarker(string taskId) => $"<!-- conductor:task {taskId} -->";

    /// <summary>The stable per-run marker planted in the diary issue's body, and in every comment
    /// so a comment can be recognised even if the issue is later edited by a human.</summary>
    public static string RunMarker(string runId) => $"<!-- conductor:run {runId} -->";

    /// <summary>The marker a single session's diary comment carries. Session numbers are unique
    /// within a run, so this is what makes "one comment per SessionFinished" idempotent.</summary>
    public static string SessionMarker(string runId, int number) =>
        $"<!-- conductor:session {runId}#{number.ToString(CultureInfo.InvariantCulture)} -->";

    /// <summary>CH4.3 — the run that OWNS a checkpoint's issue, planted alongside the task marker.
    ///
    /// <para><b>Why a second marker rather than a richer task marker.</b> <see cref="TaskMarker"/>
    /// carries the task id and nothing else, so a card issue was not attributable to a run at all —
    /// and the retire sweep, which indexes on it, closed every task-marked issue in the REPOSITORY
    /// that the run being synced did not declare. Measured 2026-08-27 on <c>shaahink/conductor</c>:
    /// every Divan and Karvansara checkpoint issue carries <c>conductor:retired</c>, closed with
    /// "no longer declared in the plan" by the run that came after them. Widening the task marker
    /// itself would change what <see cref="TaskIdIn"/> returns for every issue already on a
    /// repository; a second marker is additive, and an issue without one is simply not ours to
    /// retire — which is the safe direction to be wrong in.</para>
    ///
    /// <para>Deliberately NOT <see cref="RunMarker"/>: the diary issue is found by scanning bodies
    /// for that exact string, so a card carrying it would be adopted as the run's diary.</para>
    /// </summary>
    public static string OwnerMarker(string runId) => $"<!-- conductor:owner {runId} -->";

    /// <summary>The run id that claims a card issue, or null when the issue predates
    /// <see cref="OwnerMarker"/> or belongs to something else entirely.</summary>
    public static string? OwnerIdIn(string? body) => Between(body, "<!-- conductor:owner ", " -->");

    /// <summary>DV6.1 — the marker a BUG's issue carries. A separate marker from the checkpoint's,
    /// and that separation is the checkpoint: a bug is a different KIND of issue with a different
    /// lifetime, so the retire sweep — which reads the task marker — can never reach it when the run
    /// that filed it ends.</summary>
    public static string BugMarker(long bugId) => $"<!-- conductor:bug {bugId.ToString(CultureInfo.InvariantCulture)} -->";

    /// <summary>DV6.1 — the marker a FOLLOWUP's issue carries. Keyed on the <c>FU-</c> id, which is
    /// what <c>.conductor/followups.md</c> itself treats as the identity of a row.</summary>
    public static string FollowupMarker(string followupId) => $"<!-- conductor:followup {followupId} -->";

    /// <summary>The bug id carried by a body, or null. Used ONLY to answer "which issue is ours" —
    /// what the issue should then SAY is decided from the local ledger, never from GitHub.</summary>
    public static string? BugIdIn(string? body) => Between(body, "<!-- conductor:bug ", " -->");

    /// <summary>The followup id carried by a body, or null. Same one direction as everything else here.</summary>
    public static string? FollowupIdIn(string? body) => Between(body, "<!-- conductor:followup ", " -->");

    /// <summary>The task id carried by <paramref name="body"/>, or null when the body is not ours.
    /// Deliberately a scan for the literal marker rather than a regex: the body is arbitrary user
    /// text once a human has edited it, and a catastrophic-backtracking pattern on arbitrary text is
    /// how a sync hangs.</summary>
    public static string? TaskIdIn(string? body) => Between(body, "<!-- conductor:task ", " -->");

    /// <summary>The session marker's payload (<c>&lt;runId&gt;#&lt;number&gt;</c>) carried by a
    /// comment body, or null.</summary>
    public static string? SessionKeyIn(string? body) => Between(body, "<!-- conductor:session ", " -->");

    private static string? Between(string? body, string open, string close)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var start = body.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = body.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0) return null;
        var id = body[start..end].Trim();
        return id.Length > 0 ? id : null;
    }

    /// <summary><c>owner/name</c> for a remote URL in any form git writes it — <c>git@host:o/n.git</c>,
    /// <c>https://host/o/n.git</c>, or the already-normalised browse URL <c>Reporter.RemoteUrl</c>
    /// returns. null when the URL names no such pair (a local path remote, say).</summary>
    public static string? OwnerRepoFromUrl(string? url)
    {
        var normalised = NormaliseRemoteUrl(url);
        if (normalised is null) return null;
        var afterScheme = normalised.IndexOf("://", StringComparison.Ordinal) is var i && i >= 0
            ? normalised[(i + 3)..]
            : normalised;
        var parts = afterScheme.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // host / owner / name — anything shorter is not a repository URL.
        if (parts.Length < 3) return null;
        var owner = parts[^2];
        var name = parts[^1];
        return owner.Length > 0 && name.Length > 0 ? $"{owner}/{name}" : null;
    }

    /// <summary>A remote URL as a browsable https URL, with the <c>.git</c> suffix and the scp-style
    /// <c>git@host:</c> spelling folded away. Extracted from <c>Reporter.RemoteUrl</c>, which still
    /// owns the shelling-out and the cache and now calls this for the string work.</summary>
    public static string? NormaliseRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        if (trimmed.StartsWith("git@", StringComparison.Ordinal))
        {
            var parts = trimmed.Split('@', ':');
            if (parts.Length < 3) return null;
            return $"https://{parts[1]}/{StripGitSuffix(parts[2])}";
        }
        return StripGitSuffix(trimmed);
    }

    private static string StripGitSuffix(string s) =>
        s.EndsWith(".git", StringComparison.Ordinal) ? s[..^4] : s;

    /// <summary>Where this plan's mirror writes: the plan's explicit <c>github.repo</c> when it has
    /// one, otherwise the origin of the repo being worked on. An explicit override is what points a
    /// proof at a scratch repository instead of the real one.</summary>
    public static string? Resolve(PlanConfig plan, string? overrideRepo = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.IsNullOrWhiteSpace(overrideRepo)) return overrideRepo.Trim();
        if (!string.IsNullOrWhiteSpace(plan.Github?.Repo)) return plan.Github.Repo.Trim();
        return OwnerRepoFromUrl(Reporter.RemoteUrl(plan.Repo));
    }

    /// <summary>The token, and where it came from. Mirrors <c>TelegramService.ResolveToken</c>
    /// exactly: the environment variable wins, the plan's local secrets file is the fallback, and
    /// "neither" is an answer the caller prints rather than an exception.</summary>
    public static (string? Token, string Source) ResolveToken(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var fromEnv = Environment.GetEnvironmentVariable(TokenEnvVar)?.Trim();
        if (fromEnv is { Length: > 0 }) return (fromEnv, TokenEnvVar);
        var fromFile = SecretsStore.TryReadGithubToken(plan.StateDir);
        return fromFile is { Length: > 0 }
            ? (fromFile, SecretsPath(plan))
            : (null, "");
    }

    /// <summary>Where the file half of the credential lives, for a message that has to name it.</summary>
    public static string SecretsPath(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Path.Combine(plan.StateDir, "secrets.local.json");
    }

    /// <summary>The "no token" sentence, as data rather than as three console calls — so the bar
    /// "the refusal NAMES BOTH SOURCES" is a test rather than a reading of the source. Both places
    /// looked at are named, and so is the fact that nothing was contacted: an operator who sees this
    /// needs to know the mirror did not half-run.</summary>
    public static IReadOnlyList<string> MissingTokenRefusal(PlanConfig plan) =>
    [
        "no GitHub token. nothing was contacted.",
        $"looked at ${TokenEnvVar} (unset) and {SecretsPath(plan)} (no githubToken).",
        "a fine-grained or classic token with repo scope is enough for issues; " +
        "project is only needed for a Projects v2 board.",
    ];
}
