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

    /// <summary>The attempt budget of a stage: <c>sessions × limits.stageSlackFactor</c>, floor 1.
    /// The one copy — the loop, the verdict engine, the session runner and preflight's compose leg
    /// all read this, because four private copies of a budget is how two surfaces disagree about
    /// whether a stage is out of attempts.</summary>
    public static int MaxAttempts(PlanConfig plan, StageConfig stage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(stage);
        return Math.Max(1, stage.Sessions * plan.Limits.StageSlackFactor);
    }

    /// <summary>The whole pre-compose branch of the loop, in the loop's own order: what the next
    /// `conductor run` DOES at the top of its first iteration. <see cref="Select"/> answers "which
    /// stage"; this answers the prior question — whether a session composes at all, or the turn goes
    /// to a queued phase gate, an audit being scheduled, a park, or completion.
    /// <para>Third extraction of the same lesson as the class doc above. The first fix moved the
    /// stage RULE here and preflight's compose leg still re-implemented the branch ORDER by hand —
    /// so under <c>gatePolicy: "perPhase"</c> (the house default) it promised a Deliver session and a
    /// char count while the loop would have run the phase gate or scheduled the audit and composed
    /// nothing. The second moved the branch order and still skipped two branches whose outcome is
    /// knowable from the saved state alone: a persisted parked STATUS, which the loop idles on
    /// forever before any of this is even read, and an exhausted attempt BUDGET, on which the loop
    /// escalates (a model call when an advisor is configured, NeedsHuman when not) instead of
    /// composing. Both are modelled now. The branches live here, once, and <see cref="RunLoop"/>
    /// executes this function's answer rather than holding a private copy of it.</para>
    /// <para>Still deliberately unmodelled, because their outcome is NOT a pure function of the
    /// saved state: the pre-hook (a subprocess whose exit code decides), approval mode (the designed
    /// launch flow — the operator is present, and `conductor approve` is the next keystroke, not a
    /// failure), the DNS preflight (measures the network at spawn time), and the in-process
    /// backoffs (not persisted, so they cannot exist at launch).</para></summary>
    /// <param name="nowUtc">The clock the <see cref="RunState.BlockedUntilUtc"/> comparison uses.
    /// Null means now; a test states an instant instead.</param>
    public static LaunchDecision NextAction(PlanConfig plan, RunState state, TrackerSnapshot track, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);

        // The loop's very first act — before the tracker is read, before any branch below gets a
        // turn — is to idle on a parked status, 800ms a poll, forever. RecoverFromCrash lifts
        // Aborted, Running, VerifyingGates and Backoff at startup; these three it deliberately
        // leaves standing, so a fresh `conductor run` on them spawns nothing until
        // `conductor resume` clears the park.
        if (state.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
            return new LaunchDecision(LaunchStep.ParkedStatus, null, null, SessionKind.Deliver);

        var decision = Decide(plan, state, track);

        // An agent-declared wait (SC5.1) does not change WHAT runs next, only WHEN: the loop sleeps
        // at the session boundary until the window opens, then executes exactly this decision. Said
        // as an annotation rather than a step for that reason — and said at all because a surface
        // that names a session without naming the hours of sleep in front of it is lying by omission.
        return state.BlockedUntilUtc is { } blocked && (nowUtc ?? DateTime.UtcNow) < blocked
            ? decision with { SleepUntilUtc = blocked }
            : decision;
    }

    private static LaunchDecision Decide(PlanConfig plan, RunState state, TrackerSnapshot track)
    {
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

        // The attempt budget. The loop resets the counter when it ENTERS a stage, so exhaustion is
        // only real on the stage the state is already standing in; a queued audit gets its turn
        // regardless — the loop's own guard. What the loop DOES on this branch is not pure — it
        // consults the advisor (a model call when one is configured) and otherwise parks at
        // NeedsHuman — which is exactly why a launch drill must model that the branch FIRES: the
        // session it would otherwise promise never composes, and `conductor resume` does not reset
        // the counter (only `conductor retry-stage` and `goto` do).
        if (stage.Id == state.CurrentStage && state.PendingAudit == null
            && state.AttemptsThisStage >= MaxAttempts(plan, stage))
            return new LaunchDecision(LaunchStep.ExhaustedAttempts, stage, stage.Id, SessionKind.Deliver);

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
    /// <summary>The saved status is Paused, NeedsHuman or AwaitingOwner — the statuses
    /// <c>RecoverFromCrash</c> deliberately leaves standing. `conductor run` idles on them at the
    /// session boundary forever; `conductor resume` is the verb that lifts them.</summary>
    ParkedStatus,
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
    /// <summary>The current stage has used its whole attempt budget
    /// (<see cref="StageSelection.MaxAttempts"/>) — the loop escalates instead of composing: an
    /// advisor consult (a model call) when one is configured, a NeedsHuman park when not.
    /// `conductor retry-stage` resets the counter; `conductor resume` does not.</summary>
    ExhaustedAttempts,
    /// <summary>limits.maxSessions is reached — the run parks at the session boundary.</summary>
    SessionCap,
    /// <summary>A session composes: <see cref="LaunchDecision.Stage"/> and <see cref="LaunchDecision.Kind"/>.</summary>
    Compose,
}

/// <summary>The loop's answer, as data. <paramref name="Stage"/> is the stage the step acts on when
/// it acts on one (null for <see cref="LaunchStep.PhaseGate"/> when the queued gate names a stage the
/// plan no longer declares); <paramref name="StageId"/> is always the acted-on stage's id when there
/// is one. <paramref name="Kind"/> is meaningful only for <see cref="LaunchStep.Compose"/> — the
/// dry-run precedence: resume, then audit, then fix, then delivery.
/// <paramref name="SleepUntilUtc"/> is the agent-declared wait in front of the decision, when one is
/// saved and still in the future: the loop sleeps at the session boundary until then, and only then
/// does what the rest of this record says.</summary>
public sealed record LaunchDecision(LaunchStep Step, StageConfig? Stage, string? StageId, SessionKind Kind,
    DateTime? SleepUntilUtc = null);
