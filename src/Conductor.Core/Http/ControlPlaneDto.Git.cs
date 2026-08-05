using Conductor.Core;

namespace Conductor.Core.Http;

/// <summary>SF3.3 — one commit on the wire: the abbreviated sha the operator can paste into
/// <c>git show</c>, and the subject that says what it was.</summary>
public sealed record GitCommitDto(string Sha, string Subject);

/// <summary>
/// SF3.3 — the repo's git state on <c>GET /state</c>. Owner: <i>"git detection and awareness.
/// indicators."</i> Before this, the Face could name the repo folder and nothing else about it: not
/// the branch a run was writing to, not whether the tree was clean, not whether the work had been
/// pushed. Every one of those was a terminal round-trip away from a screen the operator was already
/// staring at.
/// <para>Absent (null) on the wire means an engine that predates this block. It does NOT mean "not a
/// git repo" — that case is present-but-empty, with <see cref="Branch"/> blank and
/// <see cref="IsRepo"/> false, because a Face must be able to say "not a git repo" out loud instead
/// of falling silent the same way it does for an old engine.</para>
/// </summary>
/// <param name="Ahead">Commits HEAD has that the upstream does not, or null when the branch has NO
/// upstream. Null is not zero: a never-pushed branch and an in-sync branch are different facts.</param>
public sealed record GitDto(
    bool IsRepo,
    string Branch,
    bool Detached,
    string? Upstream,
    int? Ahead,
    int? Behind,
    string HeadSha,
    string HeadShortSha,
    string HeadSubject,
    bool Dirty,
    int DirtyCount,
    string DirtySummary,
    IReadOnlyList<GitCommitDto> RecentCommits)
{
    public static GitDto From(GitSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // "Is this a git repo at all" is derived from whether git answered with anything — a HEAD, a
        // branch name, or a detached HEAD — rather than from a separate probe. A fresh `git init`
        // with no commits yet is a repo: it has a branch line and no sha.
        var isRepo = s.Branch.Length > 0 || s.Detached || s.HeadSha.Length > 0;
        return new GitDto(
            IsRepo: isRepo,
            Branch: s.Branch,
            Detached: s.Detached,
            Upstream: s.Upstream,
            Ahead: s.Ahead,
            Behind: s.Behind,
            HeadSha: s.HeadSha,
            HeadShortSha: s.HeadSha.Length >= 7 ? s.HeadSha[..7] : s.HeadSha,
            HeadSubject: s.HeadSubject,
            Dirty: s.Dirty,
            DirtyCount: s.DirtyCount,
            DirtySummary: s.DirtySummary,
            RecentCommits: [.. s.RecentCommits.Select(c => new GitCommitDto(c.Sha, c.Subject))]);
    }
}
