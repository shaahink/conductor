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

    /// <summary>The whole pre-compose branch of the loop, in the loop's own order: what the next
    /// `conductor run` DOES at the top of its first iteration. <see cref="Select"/> answers "which
    /// stage"; this answers the prior question — whether a session composes at all, or the turn goes
    /// to a queued phase gate, an audit being scheduled, a park, or completion.
    /// <para>Second extraction of the same lesson as the class doc above. The first fix moved the
    /// stage RULE here and preflight's compose leg still re-implemented the branch ORDER by hand —
    /// so under <c>gatePolicy: "perPhase"</c> (the house default) it promised a Deliver session and a
    /// char count while the loop would have run the phase gate or scheduled the audit and composed
    /// nothing. The branches live here now, once, and <see cref="RunLoop"/> executes this function's
    /// answer rather than holding a private copy of it.</para>
    /// <para>Only the PURE branches are modelled — the ones that decide WHAT runs next. The loop's
    /// side-effectful stops that merely pause the same decision (pre-hook failure, exhausted-attempts
    /// escalation, approval mode, the DNS preflight) park and re-decide; they never pick a different
    /// session, so a drill that cannot ask an owner anything is not lying by omitting them.</para></summary>
    public static LaunchDecision NextAction(PlanConfig plan, RunState state, TrackerSnapshot track)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);

        if (track.Checkpoints.Count == 0)
            return new LaunchDecision(LaunchStep.EmptyTracker, null, null, SessionKind.Deliver);

        if (plan.PerPhaseGates && state.PendingPhaseGate is { } gate)
            return new LaunchDecision(LaunchStep.PhaseGate,
                plan.Stages.FirstOrDefault(s => s.Id == gate.StageId), gate.StageId, SessionKind.Deliver);

        var allDone = AllEffectivelyDone(plan, state, track);
        if (allDone && !OwesASession(state))
            return new LaunchDecision(LaunchStep.ConfirmCompletion, null, null, SessionKind.Deliver);

        var stage = allDone ? Standing(plan, state) : Select(plan, state, track);
        if (stage is null)
            return new LaunchDecision(LaunchStep.NothingRunnable, null, null, SessionKind.Deliver);

        if (plan.Conventions.MentionsHuman(track.HandoffBlock))
            return new LaunchDecision(LaunchStep.HandoffEscalation, stage, stage.Id, SessionKind.Deliver);

        // The loop clears PendingFix when it enters a NEW stage (a fix does not survive the stage it
        // was queued against), and that happens before every branch below reads it.
        var fix = stage.Id == state.CurrentStage ? state.PendingFix : null;

        if (plan.PerPhaseGates && track.StageDone(stage.Id)
            && !state.ConfirmedStages.Contains(stage.Id)
            && fix == null && state.PendingResume == null
            && state.PendingVerify == null && state.PendingAudit == null)
            return new LaunchDecision(LaunchStep.ScheduleGateOrAudit, stage, stage.Id, SessionKind.Deliver);

        if (plan.Limits.MaxSessions is { } liveCap && liveCap > 0 && state.SessionCounter >= liveCap)
            return new LaunchDecision(LaunchStep.SessionCap, stage, stage.Id, SessionKind.Deliver);

        var kind = state.PendingResume != null ? SessionKind.Resume
            : state.PendingAudit != null ? SessionKind.Audit
            : fix != null ? SessionKind.Fix : SessionKind.Deliver;
        return new LaunchDecision(LaunchStep.Compose, stage, stage.Id, kind);
    }
}

/// <summary>Which branch of the run loop's pre-compose sequence fires next, in the order the loop
/// checks them. Everything before <see cref="Compose"/> means NO session composes on this turn.</summary>
public enum LaunchStep
{
    /// <summary>The tracker has no parseable checkpoint rows — the run parks at NeedsHuman.</summary>
    EmptyTracker,
    /// <summary>A queued per-phase gate runs before anything else gets a turn.</summary>
    PhaseGate,
    /// <summary>Every stage is complete or skipped and nothing is owed — the run confirms completion.</summary>
    ConfirmCompletion,
    /// <summary>No stage is runnable (what remains is skipped or blocked) — the run parks at NeedsHuman.</summary>
    NothingRunnable,
    /// <summary>The tracker handoff asks for a human — the run parks at NeedsHuman before spawning.</summary>
    HandoffEscalation,
    /// <summary>Per-phase gates: the stage's rows all read done but the stage is unconfirmed — the
    /// loop schedules the audit / full-battery phase gate instead of a session.</summary>
    ScheduleGateOrAudit,
    /// <summary>limits.maxSessions is reached — the run parks at the session boundary.</summary>
    SessionCap,
    /// <summary>A session composes: <see cref="LaunchDecision.Stage"/> and <see cref="LaunchDecision.Kind"/>.</summary>
    Compose,
}

/// <summary>The loop's answer, as data. <paramref name="Stage"/> is the stage the step acts on when
/// it acts on one (null for <see cref="LaunchStep.PhaseGate"/> when the queued gate names a stage the
/// plan no longer declares); <paramref name="StageId"/> is always the acted-on stage's id when there
/// is one. <paramref name="Kind"/> is meaningful only for <see cref="LaunchStep.Compose"/> — the
/// dry-run precedence: resume, then audit, then fix, then delivery.</summary>
public sealed record LaunchDecision(LaunchStep Step, StageConfig? Stage, string? StageId, SessionKind Kind);
