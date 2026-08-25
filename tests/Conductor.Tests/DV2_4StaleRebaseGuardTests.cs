using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// DV2.4, bug #67 — the stage-boundary squash may not rewind the branch.
///
/// <para>Measured 2026-08-14 on run <c>d6fd22ba</c>: a KILLED session left a <c>rebase-merge</c>
/// directory behind, the run carried on for 28 commits, and the next stage boundary's defensive
/// <c>git rebase --abort</c> reset the branch to the abandoned rebase's original head. The squash
/// then read the truncated range as "nothing to squash — among 2 commit(s)" and the stage
/// advanced. Nothing in the log said history had been lost.</para>
///
/// <para>These tests pin both halves of the guard: a stale state is REFUSED without being touched,
/// and a genuine interrupted rebase is still aborted as before.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DV2_4StaleRebaseGuardTests : IDisposable
{
    private readonly string _repo;

    public DV2_4StaleRebaseGuardTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-dv24-rebase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        RunGit("init", "-b", "main");
        RunGit("config", "user.email", "dv24@test");
        RunGit("config", "user.name", "DV2.4 Test");
        RunGit("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    private string RunGit(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
        return r.Output.Trim();
    }

    private string GitAllowFail(params string[] args)
        => ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(60), CancellationToken.None).Output.Trim();

    private string Commit(string file, string body, string subject)
    {
        File.WriteAllText(Path.Combine(_repo, file), body);
        RunGit("add", file);
        RunGit("commit", "-m", subject, "--no-gpg-sign");
        return RunGit("rev-parse", "HEAD");
    }

    /// <summary>The measured defect, inverted into an assertion: a rebase state directory whose
    /// recorded original head is BEHIND the branch is refused, and HEAD does not move.</summary>
    [Fact]
    public void StaleRebaseState_IsRefused_AndTheBranchKeepsEveryCommit()
    {
        var baseSha = Commit("a.txt", "1", "chore: base");
        var abandoned = Commit("a.txt", "2", "feat: where the killed session was");
        // The 28 commits of the field measurement, in miniature: work that landed AFTER the rebase
        // state was orphaned, and that an abort would delete.
        Commit("b.txt", "3", $"{Git.BookkeepingSubjectPrefix} report");
        Commit("b.txt", "4", $"{Git.BookkeepingSubjectPrefix} report");
        var tip = RunGit("rev-parse", "HEAD");

        SeedRebaseState(origHead: abandoned, headName: "refs/heads/main");

        var result = Git.SquashChoreCommits(_repo, baseSha);

        Assert.Equal(Git.SquashStatus.Refused, result.Status);
        Assert.Contains("stale rebase state", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refs/heads/main", result.Message, StringComparison.Ordinal);
        // The whole point: nothing moved, and the state git left behind is still there for a human.
        Assert.Equal(tip, RunGit("rev-parse", "HEAD"));
        Assert.NotNull(Git.RebaseStateDir(_repo));
        Assert.False(result.AbortedRebase);
    }

    /// <summary>The stage's start head must survive the abort. A rebase state whose abort rewinds
    /// HEAD past it is caught from the other side — the range this method exists to squash is gone,
    /// and "nothing to squash" is the wrong thing to tell the caller.</summary>
    [Fact]
    public void AnAbortThatRewoundPastTheStageStartHead_FailsInsteadOfReportingNothingToSquash()
    {
        Commit("a.txt", "one", "chore: base");
        var mainTip = Commit("conflict.txt", "main side", "feat: main moves");

        RunGit("checkout", "-b", "work", mainTip + "~1");
        Commit("conflict.txt", "work side", "feat: work moves");
        var stageStart = Commit("c.txt", "x", "feat: the stage starts here");

        GitAllowFail("rebase", "main");
        Assert.NotNull(Git.RebaseStateDir(_repo));

        // The abort restores `work` at its pre-rebase tip, which does contain stageStart, so this is
        // the control: the guard must NOT fire. Pass a start head the abort DOES strand instead — a
        // commit that only exists on the replay — and the failure must be loud.
        var replayed = RunGit("rev-parse", "HEAD");
        Assert.NotEqual(stageStart, replayed);

        var result = Git.SquashChoreCommits(_repo, replayed);

        Assert.Equal(Git.SquashStatus.Failed, result.Status);
        Assert.Contains("rewound HEAD past the stage's start head", result.Message, StringComparison.Ordinal);
    }

    /// <summary>The other half of the guarantee: a GENUINE interrupted rebase — one whose branch is
    /// still at the sha the abort restores — is aborted exactly as before, and the squash proceeds.
    /// Without this, the fix would be indistinguishable from deleting the recovery.</summary>
    [Fact]
    public void GenuineInterruptedRebase_IsStillAbortedAndTheSquashProceeds()
    {
        Commit("a.txt", "one", "chore: base");
        var mainTip = Commit("conflict.txt", "main side", "feat: main moves");

        RunGit("checkout", "-b", "work", mainTip + "~1");
        var workBase = RunGit("rev-parse", "HEAD");
        Commit("conflict.txt", "work side", "feat: work moves");
        Commit("c.txt", "x", $"{Git.BookkeepingSubjectPrefix} report");
        Commit("c.txt", "y", $"{Git.BookkeepingSubjectPrefix} report");
        var workTip = RunGit("rev-parse", "HEAD");

        // A rebase that stops on a conflict: the real shape of the state the recovery was written for.
        GitAllowFail("rebase", "main");
        Assert.NotNull(Git.RebaseStateDir(_repo));
        Assert.Equal(workTip, RunGit("rev-parse", "refs/heads/work"));   // git holds the branch at its start

        var result = Git.SquashChoreCommits(_repo, workBase);

        Assert.True(result.AbortedRebase, $"expected the abort to have run: {result.Message}");
        Assert.Equal(Git.SquashStatus.Squashed, result.Status);
        Assert.Null(Git.RebaseStateDir(_repo));
        // Every non-chore commit survived; only the two consecutive chore commits folded.
        var log = RunGit("log", "--format=%s", $"{workBase}..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, log.Length);
        Assert.Contains(log, l => l.Contains("work moves", StringComparison.Ordinal));
    }

    /// <summary>Write the files git itself writes, so the guard is tested against git's own format.
    /// <c>rebase --abort</c> resets <paramref name="headName"/> to <paramref name="origHead"/>.</summary>
    private void SeedRebaseState(string origHead, string headName)
    {
        var dir = Path.Combine(_repo, ".git", "rebase-merge");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "orig-head"), origHead + "\n");
        File.WriteAllText(Path.Combine(dir, "head-name"), headName + "\n");
        File.WriteAllText(Path.Combine(dir, "onto"), origHead + "\n");
    }
}
