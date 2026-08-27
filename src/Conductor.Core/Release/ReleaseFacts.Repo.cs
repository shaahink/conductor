namespace Conductor.Core.Release;

/// <summary>Everything the merge verdict is decided from. Counts, not command lines: the probe runs
/// git, this decides. Ahead/Behind follow <c>git rev-list --left-right --count base...branch</c> —
/// LEFT is base, RIGHT is branch, so <paramref name="Behind"/> is the left number.
/// <para><b>Three counts and not two,</b> because the era-close is two operations and they have
/// different merge targets. <paramref name="Behind"/> decides whether <c>git merge --ff-only</c>
/// succeeds against the LOCAL base. <paramref name="BranchBehindRemoteBase"/> decides whether the
/// push that follows is a fast-forward of the REMOTE one — the count that was measured live on this
/// repo at CH4.1, where local <c>master</c> was nine behind <c>origin/master</c> while the branch
/// already contained all nine. A verdict built on the local count alone calls that ready and a
/// verdict built on the remote count alone calls it broken; both are wrong.</para></summary>
public sealed record MergeFacts(
    string BaseBranch,
    string Branch,
    bool BaseExists,
    bool BranchExists,
    int Ahead,
    int Behind,
    int BaseBehindRemote,
    int BranchBehindRemoteBase,
    bool HasRemoteBase,
    bool Dirty);

/// <summary>What <c>tools/changelog-section.sh</c> and the file itself said. <paramref name="Version"/>
/// is null when the caller did not name one — which is not an error, it is the owner's decision
/// arriving unmade.</summary>
public sealed record ChangelogFacts(
    string? Version,
    bool FileExists,
    IReadOnlyList<string> Headings,
    bool ScriptRan,
    int ScriptExit,
    IReadOnlyList<string> SectionLines,
    string ScriptError);
