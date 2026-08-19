using Conductor.Core.Worktrees;

namespace Conductor.Core.Orchestration;

/// <summary>KS4.4 — the run's worktree housekeeping: the orphan sweep it runs at startup.</summary>
/// <remarks>It lives on the context rather than on <c>RunLoop</c> for a measured reason: RunLoop sits one
/// type under its CA1506 coupling ceiling, and startup housekeeping over the run's own repo is the
/// context's business anyway — it is the same shape as the process supervisor's orphan reap next to
/// which it is called.</remarks>
public sealed partial class RunContext
{
    /// <summary>Reap the attempt worktrees a previous run left on disk.</summary>
    /// <remarks>
    /// <para>Startup is where the leak is visible: a run killed between cutting an attempt tree and
    /// dropping it leaves a full checkout plus a git administrative record, and nothing else in the
    /// engine ever looks at either. They cost gigabytes quietly, and — the part that bites — a stale
    /// record keeps its branch name claimed, so eventually <c>git worktree add</c> refuses and new
    /// attempts stop starting.</para>
    /// <para>Never fatal. A tree whose build output is still locked by a process that outlived the run
    /// cannot be removed by anyone right now; the sweep says so and the run continues.
    /// <c>conductor worktree --reap</c> is the human's second chance at it.</para>
    /// <para>This machine hosts more than one run at a time by design, so any tree whose sidecar marker
    /// names a live process is protected — see <see cref="WorktreeSweeper"/>.</para>
    /// </remarks>
    public void SweepOrphanWorktrees()
    {
        try
        {
            var reaped = WorktreeSweeper.Reap(Plan.Repo, dryRun: false, Log);
            if (reaped.Count > 0)
                Log($"worktree sweep: {reaped.Count} orphaned attempt tree(s) from a previous run reaped");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"worktree sweep skipped: {ex.Message}", "warn");
        }
    }
}
