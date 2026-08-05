using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>SC6.2: the squash is honest and safe. Every test drives a REAL temp git repo through the
/// real <see cref="Git.SquashChoreCommits"/> — devcontext #20's defect is a property of what git ends
/// up holding, and only git can answer for it. The old implementation returned a bare bool, reported
/// success for a no-op, threw git's reason away, and refused outright on a dirty tree: four stage
/// closes of six failed silently.</summary>
public class SC62SquashTests
{
    private static string NewRepo()
    {
        var repo = Directory.CreateTempSubdirectory("conductor-sc62-").FullName;
        Git.Exec(repo, "init", "-b", "main");
        Git.Exec(repo, "config", "user.email", "sc62@rig");
        Git.Exec(repo, "config", "user.name", "SC62 Rig");
        Git.Exec(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed\n");
        Git.Exec(repo, "add", "-A");
        Git.Exec(repo, "commit", "-m", "chore: baseline");
        return repo;
    }

    private static void Commit(string repo, string file, string content, string subject)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git.Exec(repo, "add", "--", file);
        Git.Exec(repo, "commit", "-m", subject, "--", file);
    }

    private static string Head(string repo) => Git.Exec(repo, "rev-parse", "HEAD").Output.Trim();

    private static string HeadTree(string repo) => Git.Exec(repo, "rev-parse", "HEAD^{tree}").Output.Trim();

    private static List<string> Subjects(string repo)
        => Git.Exec(repo, "log", "--format=%s").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

    private static string Status(string repo) => Git.Exec(repo, "status", "--porcelain").Output.Trim();

    private static void Clean(string repo)
    {
        try { TestTemp.DeleteTree(repo); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The headline: a stage close happens on a DIRTY tree — the engine rewrites the tracker
    /// after the agent has committed it — and the old rebase refused every one of them. This squash
    /// never checks anything out, so it collapses the history while the agent's uncommitted work,
    /// staged and unstaged and untracked alike, is left byte-for-byte alone.</summary>
    [Fact]
    public void Squashes_on_a_dirty_tree_without_touching_uncommitted_work()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Commit(repo, "book.md", "one\n", "chore(conductor): s1 T0 Advanced");
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): s1 T0 Idle");
            Commit(repo, "deliverable.md", "real work\n", "feat: the agent's actual delivery");
            Commit(repo, "book.md", "one\ntwo\nthree\n", "chore(conductor): s2 T0 Advanced");
            Commit(repo, "book.md", "one\ntwo\nthree\nfour\n", "chore(conductor): s2 T0 Idle");

            // The three shapes of uncommitted work an agent leaves behind mid-flight.
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed\nedited but not staged\n");
            File.WriteAllText(Path.Combine(repo, "staged.txt"), "staged, never committed\n");
            Git.Exec(repo, "add", "--", "staged.txt");
            File.WriteAllText(Path.Combine(repo, "untracked.txt"), "untracked\n");

            var statusBefore = Status(repo);
            var treeBefore = HeadTree(repo);
            var seedBefore = File.ReadAllBytes(Path.Combine(repo, "seed.txt"));

            var result = Git.SquashChoreCommits(repo, start);

            Assert.Equal(Git.SquashStatus.Squashed, result.Status);
            // Two groups of two collapse; the feat: commit between them keeps them apart.
            Assert.Equal(5, result.CommitsBefore);
            Assert.Equal(3, result.CommitsAfter);
            Assert.Equal(2, result.Groups);
            Assert.Equal("squashed 4 chore(conductor): commits into 2 (5 commits -> 3)", result.Message);

            var subjects = Subjects(repo);
            Assert.Equal(4, subjects.Count);                                     // + the baseline
            Assert.Contains("feat: the agent's actual delivery", subjects);      // the agent's work survives
            Assert.Equal(2, subjects.Count(s => s.StartsWith("chore(conductor):", StringComparison.Ordinal)));
            // fixup semantics: the FIRST message of each group is the one that survives.
            Assert.Contains("chore(conductor): s1 T0 Advanced", subjects);
            Assert.Contains("chore(conductor): s2 T0 Advanced", subjects);

            // The tree HEAD names is the same tree, so nothing the agent left is now "changed".
            Assert.Equal(treeBefore, HeadTree(repo));
            Assert.Equal(statusBefore, Status(repo));
            Assert.Equal(seedBefore, File.ReadAllBytes(Path.Combine(repo, "seed.txt")));
            Assert.Equal("staged, never committed\n", File.ReadAllText(Path.Combine(repo, "staged.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "untracked.txt")));
            // The final collapsed commit carries the LAST commit's content, not the first's.
            Assert.Equal("one\ntwo\nthree\nfour\n", Git.Exec(repo, "show", "HEAD:book.md").Output.Replace("\r\n", "\n"));
        }
        finally { Clean(repo); }
    }

    /// <summary>The no-op the old bool lied about. Nothing adjacent, nothing squashed, and it says so
    /// with the count it actually looked at instead of a bare "complete".</summary>
    [Fact]
    public void Nothing_to_squash_is_reported_as_nothing_and_leaves_history_alone()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Commit(repo, "a.md", "a\n", "chore(conductor): lone bookkeeping");
            Commit(repo, "b.md", "b\n", "feat: work");
            var head = Head(repo);

            var result = Git.SquashChoreCommits(repo, start);

            Assert.Equal(Git.SquashStatus.NothingToSquash, result.Status);
            Assert.True(result.Ok);
            Assert.Contains("nothing to squash", result.Message, StringComparison.Ordinal);
            Assert.Contains("2 commit(s)", result.Message, StringComparison.Ordinal);
            Assert.Equal(head, Head(repo));
        }
        finally { Clean(repo); }
    }

    /// <summary>devcontext #20's silence, inverted: a failure now carries git's own exit code and its
    /// own words. The failure here is real — a start head that is not in the repo — not a stub.</summary>
    [Fact]
    public void A_failed_squash_carries_gits_exit_code_and_stderr()
    {
        var repo = NewRepo();
        try
        {
            var head = Head(repo);
            var result = Git.SquashChoreCommits(repo, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

            Assert.Equal(Git.SquashStatus.Failed, result.Status);
            Assert.False(result.Ok);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("revision", result.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(head, Head(repo));                                  // history untouched

            // ...and the engine's line quotes both, so the log is enough to diagnose it.
            var lines = new List<string>();
            var state = new RunState();
            var marked = VerdictEngine.ApplySquashResult(state, "T0", result, (m, _) => lines.Add(m));

            Assert.False(marked);
            Assert.DoesNotContain("T0", state.SquashedStages);                // retryable, not permanently marked
            var warn = Assert.Single(lines);
            Assert.Contains($"git exit {result.ExitCode}", warn, StringComparison.Ordinal);
            Assert.Contains("revision", warn, StringComparison.OrdinalIgnoreCase);
        }
        finally { Clean(repo); }
    }

    /// <summary>The other half of the marking policy: a squash that DID work marks the stage, so the
    /// next confirm does not rewrite the same history twice.</summary>
    [Fact]
    public void A_successful_squash_marks_the_stage_and_a_refusal_does_not()
    {
        var ok = new Git.SquashResult(Git.SquashStatus.Squashed, "squashed 2 chore(conductor): commits into 1 (3 commits -> 2)");
        var refused = new Git.SquashResult(Git.SquashStatus.Refused, "the range contains a merge commit");

        var state = new RunState();
        var lines = new List<string>();
        Assert.True(VerdictEngine.ApplySquashResult(state, "T0", ok, (m, _) => lines.Add(m)));
        Assert.Contains("T0", state.SquashedStages);
        Assert.Contains("squashed 2", lines[^1], StringComparison.Ordinal);

        Assert.False(VerdictEngine.ApplySquashResult(state, "T1", refused, (m, _) => lines.Add(m)));
        Assert.DoesNotContain("T1", state.SquashedStages);
        Assert.Contains("refused", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>A crashed predecessor (or the PowerShell rebase this replaced) leaves the repo mid-rebase
    /// with HEAD detached on a half-replayed commit. Rewriting from there would move the wrong ref, so
    /// the squash aborts it first and says so — the recovery devcontext #20 never had.</summary>
    [Fact]
    public void A_half_finished_rebase_is_aborted_and_the_squash_then_runs()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Commit(repo, "book.md", "one\n", "chore(conductor): s1 T0 Advanced");
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): s1 T0 Idle");
            var headBefore = Head(repo);

            // A rebase that stops on its own exec — the shape a killed engine leaves behind.
            Git.Exec(repo, "rebase", "--exec", "git no-such-verb", "HEAD~2");
            Assert.NotNull(Git.RebaseStateDir(repo));                         // precondition, not assumed

            var result = Git.SquashChoreCommits(repo, start);

            Assert.True(result.AbortedRebase);
            Assert.Null(Git.RebaseStateDir(repo));                            // no rebase left behind
            Assert.Equal(Git.SquashStatus.Squashed, result.Status);
            Assert.Equal(2, result.CommitsBefore);
            Assert.Equal(1, result.CommitsAfter);
            Assert.NotEqual(headBefore, Head(repo));

            var lines = new List<string>();
            VerdictEngine.ApplySquashResult(new RunState(), "T0", result, (m, _) => lines.Add(m));
            Assert.Contains(lines, l => l.Contains("aborted a half-finished rebase", StringComparison.Ordinal));
        }
        finally { Clean(repo); }
    }

    /// <summary>Off-Windows degradation, measured rather than promised: the squash launches nothing but
    /// git. The rebase it replaces shelled out to a generated PowerShell script with unescaped path
    /// interpolation, which could only ever run on Windows.</summary>
    [Fact]
    public void The_squash_launches_nothing_but_git()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Commit(repo, "book.md", "one\n", "chore(conductor): one");
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): two");

            var result = Git.SquashChoreCommits(repo, start);

            Assert.Equal(Git.SquashStatus.Squashed, result.Status);
            Assert.NotEmpty(result.Commands);
            Assert.All(result.Commands, c => Assert.StartsWith("git ", c, StringComparison.Ordinal));
            foreach (var shell in new[] { "powershell", "pwsh", "cmd.exe", "bash", "sh -c" })
                Assert.DoesNotContain(result.Commands, c => c.Contains(shell, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(".ps1", string.Join(" ", result.Commands), StringComparison.OrdinalIgnoreCase);
        }
        finally { Clean(repo); }
    }

    /// <summary>A merge in the range cannot be rebuilt as a straight chain without silently dropping a
    /// parent, so the squash declines and says why. Refusing is not success: the stage stays unmarked.</summary>
    [Fact]
    public void A_merge_in_the_range_is_refused_and_history_is_untouched()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Git.Exec(repo, "checkout", "-b", "side");
            Commit(repo, "side.md", "side\n", "feat: side work");
            Git.Exec(repo, "checkout", "main");
            Commit(repo, "book.md", "one\n", "chore(conductor): one");
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): two");
            Git.MergeBranch(repo, "side");
            var head = Head(repo);

            var result = Git.SquashChoreCommits(repo, start);

            Assert.Equal(Git.SquashStatus.Refused, result.Status);
            Assert.False(result.Ok);
            Assert.Contains("merge commit", result.Message, StringComparison.Ordinal);
            Assert.Equal(head, Head(repo));
        }
        finally { Clean(repo); }
    }

    /// <summary>The squash rewrites the least history it can: everything below the first fold keeps its
    /// original sha, so a reviewer's links into the stage's real commits survive where they can.</summary>
    [Fact]
    public void Commits_below_the_first_fold_keep_their_original_shas()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Commit(repo, "a.md", "a\n", "feat: first real commit");
            var untouched = Head(repo);
            Commit(repo, "book.md", "one\n", "chore(conductor): one");
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): two");

            var result = Git.SquashChoreCommits(repo, start);

            Assert.Equal(Git.SquashStatus.Squashed, result.Status);
            Assert.Equal(untouched, Git.Exec(repo, "rev-parse", "HEAD~1").Output.Trim());
            // ORIG_HEAD is left behind by name, the way a real rebase would have.
            Assert.NotEmpty(Git.Exec(repo, "rev-parse", "ORIG_HEAD").Output.Trim());
        }
        finally { Clean(repo); }
    }

    /// <summary>The message is the first commit's WHOLE message, trailers included — this repo commits
    /// with a trailer convention, and a squash that ate it would be rewriting the record.</summary>
    [Fact]
    public void The_surviving_message_keeps_its_body_and_trailers()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            File.WriteAllText(Path.Combine(repo, "book.md"), "one\n");
            Git.Exec(repo, "add", "-A");
            Git.Exec(repo, "commit", "-m", "chore(conductor): s1 T0 Advanced\n\nbody line\n\nCo-Authored-By: Rig <rig@sc62>");
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): s1 T0 Idle");

            Assert.Equal(Git.SquashStatus.Squashed, Git.SquashChoreCommits(repo, start).Status);

            var body = Git.Exec(repo, "log", "-1", "--format=%B").Output.Replace("\r\n", "\n");
            Assert.Contains("chore(conductor): s1 T0 Advanced", body, StringComparison.Ordinal);
            Assert.Contains("body line", body, StringComparison.Ordinal);
            Assert.Contains("Co-Authored-By: Rig <rig@sc62>", body, StringComparison.Ordinal);
        }
        finally { Clean(repo); }
    }

    /// <summary>Authorship is not laundered: the collapsed commit keeps the first commit's author and
    /// author date, the way an interactive fixup would.</summary>
    [Fact]
    public void The_collapsed_commit_keeps_the_first_commits_authorship()
    {
        var repo = NewRepo();
        try
        {
            var start = Head(repo);
            Commit(repo, "book.md", "one\n", "chore(conductor): one");
            var firstAuthorDate = Git.Exec(repo, "log", "-1", "--format=%aI").Output.Trim();
            Commit(repo, "book.md", "one\ntwo\n", "chore(conductor): two");

            Assert.Equal(Git.SquashStatus.Squashed, Git.SquashChoreCommits(repo, start).Status);

            Assert.Equal(firstAuthorDate, Git.Exec(repo, "log", "-1", "--format=%aI").Output.Trim());
            Assert.Equal("SC62 Rig", Git.Exec(repo, "log", "-1", "--format=%an").Output.Trim());
        }
        finally { Clean(repo); }
    }
}
