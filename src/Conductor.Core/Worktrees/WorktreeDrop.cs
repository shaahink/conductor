namespace Conductor.Core.Worktrees;

/// <summary>What dropping one attempt tree actually did — the tree, and separately the branch.</summary>
/// <remarks>Two outcomes rather than one boolean because they fail independently and mean different
/// things. A tree that would not delete is a machine problem (something holds a file) and the run can
/// carry on around it. A branch that would not delete is WORK STILL ON DISK, and the whole point of
/// KS4.4's L1.3 fix is that this case is loud rather than silent.</remarks>
public sealed record WorktreeDropResult
{
    /// <summary>The worktree directory is gone and git's record of it pruned.</summary>
    public required bool TreeRemoved { get; init; }

    /// <summary>Set when the directory would not delete — names the path that held the lock when we
    /// could tell, because "access denied" without a filename has cost this project real time.</summary>
    public string? TreeError { get; init; }

    /// <summary>The scratch/attempt branch is gone because everything on it was already reachable.</summary>
    public required bool BranchDeleted { get; init; }

    /// <summary>Set when the branch was KEPT: it held commits nothing else reaches. Carries the branch
    /// name, because that name is the only handle a human has on the work.</summary>
    public string? BranchKept { get; init; }

    /// <summary>True when nothing was lost and nothing is left behind.</summary>
    public bool Clean => TreeRemoved && BranchDeleted && BranchKept is null;
}

/// <summary>KS4.4 (lanes L1.3): the only sanctioned way to drop a conductor-owned worktree and its
/// branch.</summary>
/// <remarks>
/// <para>Two defects live here and both were real. The first: <c>git worktree remove --force</c> is a
/// directory delete wearing git's coat. On Windows the delete that actually fails is a build output —
/// a testhost holding <c>Conductor.Core.dll</c>, an msbuild node holding <c>obj/</c> — and git's
/// failure mode is to exit non-zero having already deleted an arbitrary prefix of the tree, so the
/// caller sees one opaque line and the tree is neither present nor gone. We delete the directory
/// OURSELVES, with a bounded retry and the read-only bit cleared, so the exception carries the path;
/// then <c>git worktree prune</c> reconciles git's record with the disk. Prune is safe to run
/// unconditionally: it only forgets records whose directory is missing.</para>
/// <para>The second is the one the lanes plan called its highest-value correctness fix: the branch was
/// deleted with <c>branch -D</c> in a <c>finally</c>, so a failed merge — or a merge that lost a race —
/// force-deleted a whole session of committed work with only the reflog holding it. Here the delete is
/// <c>branch -d</c>, git's own reachability check intact, and a refusal is reported as
/// <see cref="WorktreeDropResult.BranchKept"/> rather than escalated to force. An orphan branch is
/// cheap and reapable; the work is not.</para>
/// </remarks>
public static class WorktreeDrop
{
    /// <summary>How many times to retry the directory delete. Windows file locks from a just-exited
    /// process are usually released within a second or two; beyond that something is genuinely still
    /// running and retrying harder only delays the honest report.</summary>
    public const int DeleteAttempts = 5;

    /// <summary>Drop the worktree at <paramref name="path"/> and then its <paramref name="branch"/>,
    /// never losing unmerged commits.</summary>
    /// <param name="repo">The PRIMARY repo — worktree bookkeeping and the branch both live there.</param>
    /// <param name="path">The worktree directory.</param>
    /// <param name="branch">The branch the worktree was on, or null to leave branches alone.</param>
    /// <param name="log">Optional sink; the kept-branch line goes here as well as into the result.</param>
    /// <param name="sleep">Injected for tests — the retry backoff. Defaults to a real sleep.</param>
    public static WorktreeDropResult DropAttempt(
        string repo, string path, string? branch,
        Action<string>? log = null, Action<int>? sleep = null)
    {
        var (removed, treeError) = RemoveDirectory(path, sleep);
        // Prune even when the directory survived: if a PREVIOUS drop left a stale record, this is where
        // it goes. Prune keys off the directory being absent, so a surviving tree is untouched by it.
        Git.WorktreePrune(repo);
        if (!removed && treeError is not null)
            log?.Invoke($"worktree drop: {path} would not delete — {treeError}");

        if (branch is null)
            return new WorktreeDropResult { TreeRemoved = removed, TreeError = treeError, BranchDeleted = false };

        // `branch -d` refuses on an unmerged branch and that refusal is the feature. Do not escalate.
        var del = Git.DeleteBranchSafe(repo, branch);
        if (del.ExitCode == 0)
            return new WorktreeDropResult { TreeRemoved = removed, TreeError = treeError, BranchDeleted = true };

        // A branch that simply is not there is not a refusal — treat "already gone" as deleted so a
        // second drop of the same attempt does not report phantom work.
        if (!Git.BranchExists(repo, branch))
            return new WorktreeDropResult { TreeRemoved = removed, TreeError = treeError, BranchDeleted = true };

        log?.Invoke($"worktree drop: KEPT branch '{branch}' — it holds commits no other branch reaches " +
                    $"(git: {del.Output.Trim()}{del.StdErr.Trim()}). Reap it by hand once you have looked: " +
                    $"git -C \"{repo}\" branch -D {branch}");
        return new WorktreeDropResult
        {
            TreeRemoved = removed, TreeError = treeError,
            BranchDeleted = false, BranchKept = branch,
        };
    }

    /// <summary>Delete a worktree directory with the three accommodations that matter, and report the
    /// path that defeated it when it cannot.</summary>
    /// <remarks>
    /// <para>Clear the read-only bit (git writes it on pack files) and retry a few times, because the
    /// sharing violation a just-exited process leaves behind clears within a second or two.</para>
    /// <para>And — the part that took a measurement to find — the worktree's own <c>.git</c> link file is
    /// deleted LAST, after everything else is gone. A plain <c>Directory.Delete(recursive)</c> walks in
    /// filesystem order, so a lock on <c>bin/Conductor.Core.dll</c> throws having ALREADY removed
    /// <c>.git</c>; git then no longer recognises the leftover directory as a worktree, the next
    /// <c>prune</c> forgets it, and the half-deleted tree becomes invisible to the sweep that exists to
    /// finish it off. Keeping the link file until the end means a blocked drop stays a worktree git can
    /// still see, and the retry — or the next startup sweep — completes it.</para>
    /// </remarks>
    public static (bool Removed, string? Error) RemoveDirectory(string path, Action<int>? sleep = null)
    {
        if (!Directory.Exists(path)) return (true, null);
#pragma warning disable MA0045 // teardown is a synchronous finally-block concern; the sleep is injectable and bounded
        sleep ??= ms => Thread.Sleep(ms);
#pragma warning restore MA0045

        string? last = null;
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            ClearReadOnly(path);
            last = DeleteChildrenKeepingGitLink(path);
            if (last is null)
            {
                try
                {
                    // Everything else is gone; now the link file, then the directory itself.
                    var link = Path.Combine(path, GitLink);
                    if (File.Exists(link)) File.Delete(link);
                    else if (Directory.Exists(link)) Directory.Delete(link, recursive: true);
                    Directory.Delete(path, recursive: true);
                    return (true, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex.Message; }
            }
            if (attempt < DeleteAttempts) sleep(200 * attempt);
        }
        // One more look: a delete can throw on the way out having actually emptied the tree.
        return Directory.Exists(path) ? (false, last) : (true, null);
    }

    private const string GitLink = ".git";

    /// <summary>Delete every top-level entry except the <c>.git</c> link. Returns the first failure's
    /// message — which on Windows names the file that was locked — or null when the tree is empty.</summary>
    private static string? DeleteChildrenKeepingGitLink(string root)
    {
        string? first = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (string.Equals(Path.GetFileName(entry), GitLink, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                first ??= ex.Message;
            }
        }
        return first;
    }

    private static void ClearReadOnly(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(f);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
            }
            catch { /* the delete below will report it with its path */ }
        }
    }
}
