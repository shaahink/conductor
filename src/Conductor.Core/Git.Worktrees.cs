namespace Conductor.Core;

/// <summary>KS4.4 — the worktree half of <see cref="Git"/>: create, list, prune, ff-only merge and the
/// safe branch delete. Split out of Git.cs when the KS4.4 additions pushed that file past the 500-line
/// ceiling; the seam is the subject, not an arbitrary cut.</summary>
public static partial class Git
{
    // ---------------------------------------------------------------- B12.3: isolated worktrees

    /// <summary>Create a git worktree at <paramref name="path"/> on a new branch named
    /// <paramref name="branch"/> based on the current HEAD of <paramref name="repo"/>.</summary>
    public static ProcResult WorktreeAdd(string repo, string path, string branch)
        => Exec(repo, "worktree", "add", "-b", branch, path);

    /// <summary>P2: create a detached git worktree at <paramref name="path"/> pinned to
    /// <paramref name="sha"/> — read-only snapshot of the repo at that commit.</summary>
    public static ProcResult WorktreeAddDetached(string repo, string path, string sha)
        => Exec(repo, "worktree", "add", "--detach", path, sha);

    /// <summary>Remove a git worktree at <paramref name="path"/> (force cleanup even if dirty).</summary>
    /// <remarks>KS4.4: <b>do not reach for this to drop an attempt tree on Windows.</b> <c>git worktree
    /// remove --force</c> deletes the directory itself, and the one failure that actually happens here —
    /// a build output (<c>bin/</c>, <c>obj/</c>, a testhost's dll) still held by a process — makes it
    /// exit non-zero having deleted an arbitrary prefix of the tree, leaving both the directory and
    /// git's administrative record half-alive. <see cref="Worktrees.WorktreeDrop"/> deletes the
    /// directory itself with a retry that names the locking path, then prunes git's record — so a lock
    /// is REPORTED rather than swallowed. This method stays because the merge-gate staging tree it was
    /// written for is created and dropped in one breath with nothing built inside it.</remarks>
    public static ProcResult WorktreeRemove(string repo, string path)
        => Exec(repo, "worktree", "remove", path, "--force");

    /// <summary>Every worktree git knows about in <paramref name="repo"/>, as
    /// <c>git worktree list --porcelain</c> writes them: absolute path, HEAD sha, branch (null when
    /// detached), and whether git considers the path gone (<c>prunable</c>).</summary>
    public static List<WorktreeEntry> WorktreeList(string repo)
    {
        var r = Exec(repo, "worktree", "list", "--porcelain");
        var entries = new List<WorktreeEntry>();
        if (r.ExitCode != 0) return entries;

        string? path = null, head = null, branch = null; var prunable = false;
        void Flush()
        {
            if (path is not null) entries.Add(new WorktreeEntry(path, head ?? "", branch, prunable));
            path = null; head = null; branch = null; prunable = false;
        }
        foreach (var raw in r.Output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { Flush(); path = line[9..]; }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line[5..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = ShortBranch(line[7..]);
            else if (line.StartsWith("prunable", StringComparison.Ordinal)) prunable = true;
            else if (line.StartsWith("detached", StringComparison.Ordinal)) branch = null;
        }
        Flush();
        return entries;
    }

    private static string ShortBranch(string refName)
        => refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    /// <summary>Drop git's administrative record of worktree directories that no longer exist.</summary>
    public static ProcResult WorktreePrune(string repo) => Exec(repo, "worktree", "prune");

    /// <summary>Merge <paramref name="branch"/> into the current HEAD of <paramref name="repo"/> with
    /// a non-interactive merge commit. Returns the process result; exit 0 = success, non-zero = conflict.</summary>
    public static ProcResult MergeBranch(string repo, string branch)
        => Exec(repo, "merge", "--no-edit", branch);

    /// <summary>KS4.4: merge <paramref name="branch"/> <b>fast-forward only</b>. Non-zero exit means the
    /// base moved under the attempt and the caller must rebase or re-run — never a silent merge commit
    /// nobody's gates ever saw.</summary>
    public static ProcResult MergeFastForwardOnly(string repo, string branch)
        => Exec(repo, "merge", "--ff-only", branch);

    /// <summary>KS4.4 (lanes L1.3): delete a local branch with git's <b>safety check intact</b> —
    /// <c>branch -d</c>, which refuses when the branch holds commits no other branch has.</summary>
    /// <remarks>
    /// <para>This used to be <c>branch -D</c>, called from <c>MutatingLaneRunner</c>'s <c>finally</c>
    /// block. Read that together: a lane whose merge gate went red, or whose merge lost a race to
    /// another lane, had a full session of committed work force-deleted on the way out, with only the
    /// reflog — which expires — holding it. The lanes plan named this its highest-value correctness
    /// fix (L1.3); ND-8 lands it here, where the same branch now carries a stage ATTEMPT.</para>
    /// <para>There is deliberately no force overload. The caller's answer to a refusal is to KEEP the
    /// branch and say its name (see <see cref="Worktrees.WorktreeDrop.DropAttempt"/>): an orphan branch
    /// costs a few bytes and is reapable by a human at leisure, and losing the work is not reversible
    /// at all. The asymmetry is the whole point.</para>
    /// </remarks>
    public static ProcResult DeleteBranchSafe(string repo, string branch)
        => Exec(repo, "branch", "-d", branch);

    /// <summary>True when every commit on <paramref name="branch"/> is reachable from
    /// <paramref name="into"/> — i.e. deleting the branch loses nothing.</summary>
    public static bool IsBranchMergedInto(string repo, string branch, string into)
    {
        var r = Exec(repo, "rev-list", "--count", $"{into}..{branch}");
        return r.ExitCode == 0 && int.TryParse(r.Output.Trim(), out var n) && n == 0;
    }

    /// <summary>True when <paramref name="branch"/> resolves to a commit in <paramref name="repo"/>.</summary>
    public static bool BranchExists(string repo, string branch)
        => Exec(repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
}

/// <summary>One row of <c>git worktree list --porcelain</c>. <paramref name="Prunable"/> is git's own
/// word for "the directory is gone but I still hold a record of it" — the shape a killed run leaves.</summary>
public sealed record WorktreeEntry(string Path, string Head, string? Branch, bool Prunable);
