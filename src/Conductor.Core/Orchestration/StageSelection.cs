using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// Which stage a run lands on next, as a pure function of the plan, the saved state and the tracker.
/// <para>This used to live only inside <see cref="RunLoop"/> as four private methods, which was fine
/// while the loop was the only thing that had to answer the question. It stopped being fine at KS3.4:
/// <c>preflight</c>'s compose leg names the next session BEFORE anything spawns, and a second copy of
/// the rule — one that read the current stage and the tracker but not <c>skippedStages</c>, not
/// <c>dependsOn</c> and not <c>confirmedStages</c> under per-phase gates — printed a different stage
/// than <c>run --dry-run</c> printed for the same plan, and then measured a different stage's prompt.
/// A drill whose whole purpose is truth before launch cannot own a private opinion about what will
/// launch, so the rule is stated once, here, and read by both.</para>
/// <para>Read-only by construction: nothing on this type touches the disk, the store or the state it
/// is handed.</para>
/// </summary>
public static class StageSelection
{
    /// <summary>Has this stage been finished? Under per-phase gates "finished" means CONFIRMED — the
    /// full battery passed and the audit closed — because a stage whose rows all read done has not
    /// yet been through its gate. Otherwise the tracker's own done-ness is the answer.</summary>
    public static bool IsComplete(PlanConfig plan, RunState state, TrackerSnapshot track, string stageId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);
        return plan.PerPhaseGates ? state.ConfirmedStages.Contains(stageId) : track.StageDone(stageId);
    }

    /// <summary>A dependency is satisfied when it is complete OR when the owner skipped it — a skip is
    /// a decision that the work will not happen, not a promise that it will happen later.</summary>
    public static bool DependencySatisfied(PlanConfig plan, RunState state, TrackerSnapshot track, string stageId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return IsComplete(plan, state, track, stageId) || state.SkippedStages.Contains(stageId);
    }

    /// <summary>Could the loop start a session on this stage right now: not complete, not skipped, and
    /// every declared <c>dependsOn</c> satisfied.</summary>
    public static bool IsReady(PlanConfig plan, RunState state, TrackerSnapshot track, StageConfig stage)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stage);
        if (IsComplete(plan, state, track, stage.Id) || state.SkippedStages.Contains(stage.Id))
            return false;
        return stage.DependsOn is not { Count: > 0 }
            || stage.DependsOn.All(d => DependencySatisfied(plan, state, track, d));
    }

    /// <summary>The stage the next session runs on: the first READY stage in declaration order. Null
    /// when nothing is runnable — either everything is done or what is left is blocked, which the loop
    /// treats as needing a human rather than as completion.</summary>
    public static StageConfig? Select(PlanConfig plan, RunState state, TrackerSnapshot track)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Stages.FirstOrDefault(s => IsReady(plan, state, track, s));
    }

    /// <summary>Is there no work left at all — every stage complete or skipped? The loop's completion
    /// guard, and the reason a skipped tail does not hold a finished run open forever.</summary>
    public static bool AllEffectivelyDone(PlanConfig plan, RunState state, TrackerSnapshot track)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        return plan.Stages.All(s => IsComplete(plan, state, track, s.Id) || state.SkippedStages.Contains(s.Id));
    }

    /// <summary>Where the loop stands when everything reads done but a session is still owed (a queued
    /// resume, audit, fix or verification): the stage the state is already in, else the last declared
    /// one. Mirrors <see cref="RunLoop"/>'s own fallback, which is the only place a stage is chosen
    /// without being ready.</summary>
    public static StageConfig? Standing(PlanConfig plan, RunState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        if (plan.Stages.Count == 0) return null;
        return plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage) ?? plan.Stages[^1];
    }

    /// <summary>Does this run still owe a session that completion must stand aside for? W5.1: a queued
    /// verification or audit is work the run owes, and a guard that named only fix and resume skipped
    /// the last checkpoint's verification.</summary>
    public static bool OwesASession(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PendingFix != null || state.PendingResume != null
            || state.PendingVerify != null || state.PendingAudit != null;
    }
}
