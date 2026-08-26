namespace Conductor.Core.Integrations.Cloud;

/// <summary>Why <c>/cloud</c> would be running the agent on something other than what the owner is
/// looking at. Each value is one named refusal, in the order it is checked.</summary>
public enum CloudPreflightVerdict
{
    /// <summary>The remote has exactly what this checkout has, and nothing is uncommitted.</summary>
    Ok = 0,
    /// <summary>No commit for a cloud session to clone: not a checkout at all, or a checkout with
    /// no commits yet. One verdict because the instruction to the owner is the same.</summary>
    NothingToClone = 1,
    DetachedHead = 2,
    DirtyTree = 3,
    NoUpstream = 4,
    RemoteMissingBranch = 5,
    RemoteDiffersFromHead = 6,
}

/// <param name="Detail">The exact git state that decided it, in the words a chat reply quotes. Not a
/// summary — findings §6.8 asks for the state itself, because "your tree is dirty" from a phone is
/// an instruction to go and look, and "M src/Foo.cs (3 files)" is an answer.</param>
public sealed record CloudPreflightResult(
    CloudPreflightVerdict Verdict, string Branch, string HeadSha, string? RemoteSha, string Detail)
{
    public bool Ok => Verdict == CloudPreflightVerdict.Ok;
}

/// <summary>DV5.1 / findings §6.8 — the preflight a cloud session needs and a local session does not.
///
/// <para>§2.4 item 4: a cloud session CLONES FROM THE REMOTE. Uncommitted work is invisible to it and
/// an unpushed commit is invisible to it, so a <c>/cloud</c> fired against a dirty tree silently runs
/// the agent on yesterday's code and hands back a confident answer about a file that no longer says
/// that. Nothing downstream can detect it. The only place to catch it is before the spawn.</para>
///
/// <para>The remote tip is read with <c>git ls-remote</c> rather than from the tracking counters,
/// because <c>git status</c>'s ahead/behind is measured against the last <c>git fetch</c> — a branch
/// that says "up to date" against a ref fetched an hour ago is exactly the state this check exists to
/// refuse. <see cref="Judge"/> is pure so every verdict is asserted without a network or a repo.</para></summary>
public static class CloudPreflight
{
    /// <summary>The decision, over facts already measured.</summary>
    /// <param name="remoteTipSha">What the remote says the branch points at, or null when the remote
    /// does not have the branch (or would not answer).</param>
    public static CloudPreflightResult Judge(GitSnapshot snapshot, string? remoteTipSha)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.HeadSha.Length == 0 || (snapshot.Branch.Length == 0 && !snapshot.Detached))
            return new CloudPreflightResult(CloudPreflightVerdict.NothingToClone, "", "", null,
                "there is no commit there for a cloud session to clone — the path is not a git checkout, or it has no commits yet.");

        if (snapshot.Detached)
            return new CloudPreflightResult(CloudPreflightVerdict.DetachedHead, "", snapshot.HeadSha, null,
                $"HEAD is detached at {Short(snapshot.HeadSha)} — a cloud session clones a BRANCH, and there is none to name.");

        if (snapshot.Dirty)
            return new CloudPreflightResult(CloudPreflightVerdict.DirtyTree, snapshot.Branch, snapshot.HeadSha, null,
                $"{snapshot.Branch} has {snapshot.DirtyCount} uncommitted change{(snapshot.DirtyCount == 1 ? "" : "s")}:\n{snapshot.DirtySummary}");

        if (snapshot.Upstream is not { Length: > 0 } upstream)
            return new CloudPreflightResult(CloudPreflightVerdict.NoUpstream, snapshot.Branch, snapshot.HeadSha, null,
                $"{snapshot.Branch} has no upstream — it has never been pushed, so the remote has no copy of it at all.");

        if (remoteTipSha is not { Length: > 0 })
            return new CloudPreflightResult(CloudPreflightVerdict.RemoteMissingBranch, snapshot.Branch, snapshot.HeadSha, null,
                $"{upstream} does not answer for {snapshot.Branch}; the remote has no such branch right now.");

        if (!string.Equals(remoteTipSha, snapshot.HeadSha, StringComparison.OrdinalIgnoreCase))
            return new CloudPreflightResult(CloudPreflightVerdict.RemoteDiffersFromHead, snapshot.Branch, snapshot.HeadSha, remoteTipSha,
                $"{snapshot.Branch} is at {Short(snapshot.HeadSha)} but {upstream} is at {Short(remoteTipSha)}"
                + AheadBehind(snapshot) + " — a cloud session would clone the remote's commit, not yours.");

        return new CloudPreflightResult(CloudPreflightVerdict.Ok, snapshot.Branch, snapshot.HeadSha, remoteTipSha,
            $"{snapshot.Branch} at {Short(snapshot.HeadSha)}, clean, and {upstream} has the same commit.");
    }

    /// <summary>The production read: a fresh snapshot (never the 4-second cache — the owner has just
    /// typed a command about the state of the tree) and one <c>ls-remote</c>.</summary>
    public static CloudPreflightResult Probe(string repoDir)
    {
        if (string.IsNullOrWhiteSpace(repoDir) || !Directory.Exists(repoDir))
            return Judge(GitSnapshot.None, null);

        var snapshot = GitSnapshot.Probe(repoDir);
        return Judge(snapshot, RemoteTip(repoDir, snapshot));
    }

    /// <summary>What the remote says the branch points at, right now. Null on any failure — an
    /// unreachable remote is refused as "no such branch there", which is the same instruction to the
    /// owner (go and push, or go and look) and never a silent pass.</summary>
    public static string? RemoteTip(string repoDir, GitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Upstream is not { Length: > 0 } upstream || snapshot.Branch.Length == 0) return null;

        var slash = upstream.IndexOf('/', StringComparison.Ordinal);
        var remote = slash > 0 ? upstream[..slash] : upstream;

        var r = Git.Exec(repoDir, "ls-remote", "--heads", remote, "refs/heads/" + snapshot.Branch);
        if (r.ExitCode != 0) return null;

        var line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var sha = line?.Split('\t', ' ')[0].Trim();
        return sha is { Length: 40 } ? sha : null;
    }

    private static string AheadBehind(GitSnapshot s) =>
        (s.Ahead, s.Behind) switch
        {
            (> 0, > 0) => $" (locally {s.Ahead} ahead, {s.Behind} behind at last fetch)",
            (> 0, _) => $" (locally {s.Ahead} ahead at last fetch)",
            (_, > 0) => $" (locally {s.Behind} behind at last fetch)",
            _ => "",
        };

    private static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;
}
