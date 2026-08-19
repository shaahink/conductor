using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Worktrees;

namespace Conductor.Tests;

/// <summary>
/// KS4.4 — worktree-per-stage-attempt, and the lanes-plan L1.3 correctness fix that rides with it (ND-8).
/// </summary>
/// <remarks>
/// These drive REAL git repositories on disk rather than asserting from source reading, because every
/// claim here is a claim about what git and the filesystem actually do: that `branch -d` refuses, that a
/// held file handle defeats a directory delete, that `merge --ff-only` declines a diverged base. A test
/// that mocked those would prove only that the mock agrees with the doc comment.
/// </remarks>
public class KS4_4WorktreeAttemptTests
{
    // ---------------------------------------------------------------- a failed attempt is a mechanical rollback

    [Fact]
    [Trait("Category", "Integration")]
    public void A_failed_attempt_is_dropped_whole_and_the_primary_tree_is_untouched()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var headBefore = Git.Head(repo);
            var filesBefore = TrackedTreeFiles(repo);

            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "run-test");
            Assert.NotNull(wt);
            // The attempt does what a delivery session does: commits, and leaves something uncommitted.
            File.WriteAllText(Path.Combine(wt!.Path, "delivered.cs"), "// work\n");
            Git.Exec(wt.Path, "add", "delivered.cs");
            Git.Exec(wt.Path, "commit", "-m", "feat: the attempt's work");
            File.WriteAllText(Path.Combine(wt.Path, "scratch.txt"), "half-finished\n");

            // The primary tree never saw any of it, mid-attempt.
            Assert.Equal(headBefore, Git.Head(repo));
            Assert.False(File.Exists(Path.Combine(repo, "delivered.cs")));

            var dropped = wt.Drop();

            Assert.True(dropped.TreeRemoved);
            Assert.False(Directory.Exists(wt.Path));
            // Rollback is mechanical: HEAD and the file set are byte-for-byte what they were.
            Assert.Equal(headBefore, Git.Head(repo));
            Assert.Equal(filesBefore, TrackedTreeFiles(repo));
            Assert.False(File.Exists(Path.Combine(repo, "delivered.cs")));
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- L1.3: never force-delete an unmerged branch

    [Fact]
    [Trait("Category", "Integration")]
    public void A_failed_attempts_branch_survives_the_drop_and_its_name_is_reported()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "run-test");
            Assert.NotNull(wt);
            File.WriteAllText(Path.Combine(wt!.Path, "session-of-work.cs"), "// an entire session\n");
            Git.Exec(wt.Path, "add", "session-of-work.cs");
            Git.Exec(wt.Path, "commit", "-m", "feat: work that must not be force-deleted");
            var attemptSha = Git.Head(wt.Path);

            var log = new List<string>();
            var dropped = wt.Drop(log.Add);

            // The old code force-deleted this branch in a finally block. It is still here.
            Assert.False(dropped.BranchDeleted);
            Assert.Equal(wt.Branch, dropped.BranchKept);
            Assert.True(Git.BranchExists(repo, wt.Branch));
            Assert.Equal(attemptSha, Git.Exec(repo, "rev-parse", wt.Branch).Output.Trim());
            // And the name reached the log — the only handle a human has on the work.
            Assert.Contains(log, l => l.Contains(wt.Branch, StringComparison.Ordinal) && l.Contains("KEPT", StringComparison.Ordinal));

            Git.Exec(repo, "branch", "-D", wt.Branch);   // test-local cleanup, not engine behaviour
        }
        finally { cleanup(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void An_attempt_that_committed_nothing_takes_its_branch_with_it()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 2, runId: "run-test");
            Assert.NotNull(wt);
            File.WriteAllText(Path.Combine(wt!.Path, "never-committed.txt"), "nothing reached a commit\n");

            var dropped = wt.Drop();

            Assert.True(dropped.Clean);
            Assert.True(dropped.BranchDeleted);
            Assert.Null(dropped.BranchKept);
            Assert.False(Git.BranchExists(repo, wt.Branch));
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- ff-only merge on green

    [Fact]
    [Trait("Category", "Integration")]
    public void A_green_attempt_fast_forwards_into_the_primary_tree_and_then_drops_clean()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "run-test");
            Assert.NotNull(wt);
            File.WriteAllText(Path.Combine(wt!.Path, "shipped.cs"), "// green\n");
            Git.Exec(wt.Path, "add", "shipped.cs");
            Git.Exec(wt.Path, "commit", "-m", "feat: shipped");
            var attemptSha = Git.Head(wt.Path);
            Assert.True(wt.HasCommits());

            var merged = wt.MergeIntoPrimary();

            Assert.Equal(0, merged.ExitCode);
            Assert.Equal(attemptSha, Git.Head(repo));          // fast-forward: no merge commit invented
            Assert.True(File.Exists(Path.Combine(repo, "shipped.cs")));

            // Now that the work is reachable from the primary branch, the safe delete accepts it.
            var dropped = wt.Drop();
            Assert.True(dropped.Clean);
            Assert.False(Git.BranchExists(repo, wt.Branch));
        }
        finally { cleanup(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void The_merge_refuses_rather_than_inventing_a_commit_when_the_base_moved_under_the_attempt()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "run-test");
            Assert.NotNull(wt);
            File.WriteAllText(Path.Combine(wt!.Path, "attempt.cs"), "// attempt\n");
            Git.Exec(wt.Path, "add", "attempt.cs");
            Git.Exec(wt.Path, "commit", "-m", "feat: attempt work");

            // The primary tree moves — someone else's commit, a bookkeeping commit, a rebase.
            File.WriteAllText(Path.Combine(repo, "elsewhere.txt"), "the base moved\n");
            Git.Exec(repo, "add", "elsewhere.txt");
            Git.Exec(repo, "commit", "-m", "chore: base moved");
            var primaryHead = Git.Head(repo);

            var merged = wt.MergeIntoPrimary();

            // --ff-only declines. The gates went green on a tree that no longer exists, so a merge
            // commit here would launder an unverified combination into the branch.
            Assert.NotEqual(0, merged.ExitCode);
            Assert.Equal(primaryHead, Git.Head(repo));
            Assert.False(File.Exists(Path.Combine(repo, "attempt.cs")));

            wt.Drop();
            Git.Exec(repo, "branch", "-D", wt.Branch);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- the clean attempt diff the verdict receives

    [Fact]
    [Trait("Category", "Integration")]
    public void The_attempt_diff_carries_committed_uncommitted_and_brand_new_work_and_nothing_from_the_primary_tree()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "run-test");
            Assert.NotNull(wt);

            File.WriteAllText(Path.Combine(wt!.Path, "committed.cs"), "// committed inside the attempt\n");
            Git.Exec(wt.Path, "add", "committed.cs");
            Git.Exec(wt.Path, "commit", "-m", "feat: committed");
            File.WriteAllText(Path.Combine(wt.Path, "README.md"), "# Test Repo\nedited by the attempt\n");
            File.WriteAllText(Path.Combine(wt.Path, "brand-new.cs"), "// never added to git\n");

            // Meanwhile the primary tree gains a commit of its own — the engine's own bookkeeping.
            File.WriteAllText(Path.Combine(repo, "not-the-attempts.txt"), "engine bookkeeping\n");
            Git.Exec(repo, "add", "not-the-attempts.txt");
            Git.Exec(repo, "commit", "-m", "chore(conductor): bookkeeping");

            var diff = wt.AttemptDiff();

            Assert.Contains("committed.cs", diff, StringComparison.Ordinal);
            Assert.Contains("edited by the attempt", diff, StringComparison.Ordinal);
            Assert.Contains("brand-new.cs", diff, StringComparison.Ordinal);
            Assert.Contains("never added to git", diff, StringComparison.Ordinal);
            // The diff is measured from the sha the tree was cut at, so the primary tree's own commit
            // cannot leak into the attempt's evidence.
            Assert.DoesNotContain("not-the-attempts.txt", diff, StringComparison.Ordinal);

            var changed = wt.ChangedFiles();
            Assert.Contains("committed.cs", changed);
            Assert.Contains("brand-new.cs", changed);
            Assert.DoesNotContain("not-the-attempts.txt", changed);

            wt.Drop();
            Git.Exec(repo, "branch", "-D", wt.Branch);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- Windows: a locked build output is REPORTED, not swallowed

    [Fact]
    [Trait("Category", "Integration")]
    public void A_locked_file_in_the_tree_is_reported_by_path_and_the_tree_is_reapable_once_it_clears()
    {
        var (repo, cleanup) = CreateTestRepo();
        AttemptWorktree? wt = null;
        try
        {
            wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "run-test");
            Assert.NotNull(wt);
            var binDir = Path.Combine(wt!.Path, "bin");
            Directory.CreateDirectory(binDir);
            var locked = Path.Combine(binDir, "Conductor.Core.dll");
            File.WriteAllText(locked, "pretend assembly\n");

            WorktreeDropResult dropped;
            var noSleep = new Action<int>(_ => { });
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                dropped = WorktreeDrop.DropAttempt(repo, wt.Path, wt.Branch, sleep: noSleep);
            }

            if (OperatingSystem.IsWindows())
            {
                // The honest report is the whole point: `git worktree remove --force` returns one
                // opaque line here having deleted an arbitrary prefix of the tree.
                Assert.False(dropped.TreeRemoved);
                Assert.NotNull(dropped.TreeError);
                Assert.Contains("Conductor.Core.dll", dropped.TreeError!, StringComparison.OrdinalIgnoreCase);
                Assert.True(Directory.Exists(wt.Path));

                // Git's record must NOT have been pruned while the directory is still there — otherwise
                // the next sweep cannot see the tree it still has to reap.
                Assert.Contains(Git.WorktreeList(repo),
                    e => string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(wt.Path), StringComparison.OrdinalIgnoreCase));

                // Handle released: the same drop now completes.
                var second = WorktreeDrop.DropAttempt(repo, wt.Path, wt.Branch, sleep: noSleep);
                Assert.True(second.TreeRemoved);
                Assert.False(Directory.Exists(wt.Path));
            }
            else
            {
                // POSIX lets an open file be unlinked; the drop simply succeeds. Asserted so the test
                // states which platform the guarantee is measured on rather than silently skipping.
                Assert.True(dropped.TreeRemoved);
            }
        }
        finally
        {
            if (wt is not null) { WorktreeDrop.DropAttempt(repo, wt.Path, null); Git.Exec(repo, "branch", "-D", wt.Branch); }
            cleanup();
        }
    }

    // ---------------------------------------------------------------- the orphan sweep

    [Fact]
    [Trait("Category", "Integration")]
    public void The_sweep_reaps_an_attempt_tree_whose_run_is_gone()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "dead-run");
            Assert.NotNull(wt);
            MarkOwnerDead(wt!.Path);

            var survey = WorktreeSweeper.Survey(repo);
            var mine = Assert.Single(survey, s => string.Equals(Path.GetFullPath(s.Entry.Path), Path.GetFullPath(wt.Path), StringComparison.OrdinalIgnoreCase));
            Assert.True(mine.ConductorOwned);
            Assert.True(mine.Reapable);

            var planned = WorktreeSweeper.Reap(repo, dryRun: true);
            Assert.Contains(planned, l => l.StartsWith("would reap", StringComparison.Ordinal));
            Assert.True(Directory.Exists(wt.Path));   // a dry run touches nothing

            var reaped = WorktreeSweeper.Reap(repo);

            Assert.Single(reaped);
            Assert.False(Directory.Exists(wt.Path));
            Assert.DoesNotContain(WorktreeSweeper.Survey(repo),
                s => string.Equals(Path.GetFullPath(s.Entry.Path), Path.GetFullPath(wt.Path), StringComparison.OrdinalIgnoreCase));
        }
        finally { cleanup(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void The_sweep_leaves_a_humans_worktree_alone()
    {
        var (repo, cleanup) = CreateTestRepo();
        var human = Path.Combine(Path.GetTempPath(), $"someones-feature-{Guid.NewGuid():N}"[..40]);
        try
        {
            Assert.Equal(0, Git.WorktreeAdd(repo, human, "feature/someones-branch").ExitCode);

            var survey = WorktreeSweeper.Survey(repo);
            var theirs = Assert.Single(survey, s => string.Equals(Path.GetFullPath(s.Entry.Path), Path.GetFullPath(human), StringComparison.OrdinalIgnoreCase));
            Assert.False(theirs.ConductorOwned);
            Assert.False(theirs.Reapable);

            var reaped = WorktreeSweeper.Reap(repo);

            Assert.Empty(reaped);
            Assert.True(Directory.Exists(human));
            Assert.True(Git.BranchExists(repo, "feature/someones-branch"));
        }
        finally
        {
            WorktreeDrop.DropAttempt(repo, human, null);
            cleanup();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void The_sweep_protects_a_live_runs_attempt_tree()
    {
        var (repo, cleanup) = CreateTestRepo();
        AttemptWorktree? wt = null;
        try
        {
            // The marker written by Create names THIS process, which is alive by definition.
            wt = AttemptWorktree.Create(repo, "KS4", attempt: 1, runId: "live-run");
            Assert.NotNull(wt);

            var mine = Assert.Single(WorktreeSweeper.Survey(repo), s => s.ConductorOwned);
            Assert.True(mine.OwnerAlive);
            Assert.False(mine.Reapable);
            Assert.Equal(1, WorktreeSweeper.LiveCount(repo));

            Assert.Empty(WorktreeSweeper.Reap(repo));
            Assert.True(Directory.Exists(wt!.Path));
        }
        finally
        {
            if (wt is not null) wt.Drop();
            cleanup();
        }
    }

    // ---------------------------------------------------------------- the rule, as a rule

    [Fact]
    public void No_engine_source_file_force_deletes_a_git_branch()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            foreach (var (line, n) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                // Code, not prose: a doc comment may name `branch -D` (WorktreeDrop's explanation of
                // the fix does, and the human-facing "reap it by hand" line prints it).
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)
                    || code.StartsWith("/*", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal)) continue;
                if (line.Contains("\"branch\", \"-D\"", StringComparison.Ordinal)
                    || line.Contains("\"branch\", \"--delete\", \"--force\"", StringComparison.Ordinal)
                    || line.Contains("\"-D\", ", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{n}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "KS4.4 / lanes L1.3: `git branch -D` force-deletes an UNMERGED branch and loses a whole session " +
            "of committed work with only the reflog holding it. Use Git.DeleteBranchSafe (or WorktreeDrop), " +
            "which keeps git's reachability check and REPORTS a refusal.\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_drop_path_does_not_reach_for_git_worktree_remove()
    {
        var drop = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor.Core", "Worktrees", "WorktreeDrop.cs"));
        // `worktree remove --force` swallows the exact IO exception a locked build output produces.
        Assert.DoesNotContain("Git.WorktreeRemove", drop, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete", drop, StringComparison.Ordinal);
        Assert.Contains("Git.WorktreePrune", drop, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Rewrite the sidecar so its owning pid reads as gone. Uses THIS pid with a start time
    /// five years in the past, which <see cref="PidLiveness"/> classifies as Recycled — a stronger
    /// signal than an invented pid, which a real process could coincidentally hold.</summary>
    private static void MarkOwnerDead(string worktreePath)
    {
        var path = worktreePath + ".attempt.json";
        var marker = JsonSerializer.Deserialize<AttemptMarker>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(marker with { PidStartUtc = DateTime.UtcNow.AddYears(-5) }));
    }

    private static HashSet<string> TrackedTreeFiles(string repo)
        => new(Directory.GetFiles(repo, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repo, f))
            .Where(f => !f.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            StringComparer.OrdinalIgnoreCase);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (string repoPath, Action cleanup) CreateTestRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-ks4_4-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(repo);
        Git.Exec(repo, "init", "-b", "main");
        Git.Exec(repo, "config", "user.email", "conductor@test.local");
        Git.Exec(repo, "config", "user.name", "Conductor Test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Test Repo\n");
        Git.Exec(repo, "add", "README.md");
        Git.Exec(repo, "commit", "-m", "initial commit");

        void Cleanup()
        {
            try
            {
                foreach (var s in WorktreeSweeper.Survey(repo)) WorktreeDrop.DropAttempt(repo, s.Entry.Path, null);
            }
            catch { }
            try { WorktreeDrop.RemoveDirectory(repo); } catch { }
        }
        return (repo, Cleanup);
    }
}
