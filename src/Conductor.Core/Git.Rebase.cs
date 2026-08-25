namespace Conductor.Core;

public static partial class Git
{
    /// <summary>
    /// DV2.4, bug #67 — is the <c>rebase-merge</c>/<c>rebase-apply</c> state in this repo STALE,
    /// meaning <c>git rebase --abort</c> would REWIND the branch instead of recovering it? Returns
    /// the reason it is stale, or null when the state belongs to a rebase git is genuinely inside.
    ///
    /// <para>Measured 2026-08-14, run <c>d6fd22ba</c>: a KILLED session left a rebase state directory
    /// behind; the run carried on and the branch moved 28 commits past it. The next stage boundary's
    /// defensive <c>rebase --abort</c> reset the branch to the abandoned rebase's original head —
    /// HEAD moved BACK 28 commits, the squash then read the truncated range as "nothing to squash —
    /// among 2 commit(s)", and the stage advanced. Nothing in the log said history had been lost.</para>
    ///
    /// <para>The test is HEAD's attachment, and it is exact rather than heuristic: for the whole of a
    /// rebase git holds the rebased branch at its starting point and replays onto a DETACHED HEAD —
    /// that is what <c>head-name</c> in the state directory is for. So a repository that has a rebase
    /// state directory AND a HEAD attached to a branch is not mid-rebase; the state is litter from a
    /// process that died, the branch has moved on under it, and an abort resets that branch to a sha
    /// from before the work. Refusing costs a parked stage; aborting costs commits.</para>
    ///
    /// <para>It reads no files out of <c>.git</c> on purpose — one <c>symbolic-ref</c> asks git the
    /// question directly, and the alternative (parsing <c>orig-head</c>/<c>head-name</c>) would put
    /// this guard's correctness at the mercy of a state directory format git does not promise.</para>
    ///
    /// <para>The narrow case it cannot see is a dead rebase whose HEAD is still detached and has been
    /// committed onto since. <see cref="SquashChoreCommits"/> covers that from the other side: after
    /// any abort the stage's own start head must still be an ancestor of HEAD.</para>
    /// </summary>
    internal static string? StaleRebaseReason(string repo, string stateDir)
    {
        var symref = Exec(repo, "symbolic-ref", "-q", "HEAD");
        if (symref.ExitCode != 0) return null;   // detached: a rebase really is in progress

        var branch = symref.Output.Trim();
        return $"HEAD is attached to {(branch.Length == 0 ? "a branch" : branch)}, so no rebase is in " +
               $"progress — the {Path.GetFileName(stateDir)} directory is litter from a process that died, " +
               "and aborting it would reset that branch to the abandoned rebase's starting point";
    }
}
