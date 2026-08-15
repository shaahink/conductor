using Conductor.Core.Events;
using Conductor.Models;
using Conductor.Planning;

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
    /// <para>Rounds 4 and 5, the same lesson from the other side: sharing the FUNCTION is not
    /// sharing the decision until the INPUTS are shared too — and the loop MUTATES both inputs
    /// before it reads them. <paramref name="track"/> must be the work snapshot the loop schedules
    /// on AFTER its startup sync: <c>RunLoop.RunAsync</c> runs <see cref="Planning.WorkGraphSync"/>
    /// before its first read, so the snapshot is the declared row set carrying the graph's statuses
    /// (plus scaffolds, minus retirements) — never the declaration alone (frozen at TODO on an
    /// imported plan, round 4) and never the graph at rest (blind to every row declared since the
    /// last session, round 5). <paramref name="state"/> must be the state after
    /// <see cref="CrashRecovery.Apply"/> AND, when that recovers nothing, after
    /// <see cref="CrashRecovery.ApplyOrphan"/> — the loop's store-backed orphan recovery queues a
    /// resume (or parks) off the event log before its first decision. The loop reads all of this
    /// through <c>RunContext.ReadWork</c> and <c>RecoverFromCrash</c>; <c>preflight</c> reads the
    /// same functions at rest (<see cref="Planning.WorkSnapshot.ReadAtRest"/> and
    /// <see cref="CrashRecovery.ApplyOrphan"/> over the same <c>run.db</c>, read-only, on the
    /// peeked state).</para>
    /// <para>Round 6, the far side of the decision: the loop's session KIND was not finished by this
    /// function either. With no pending state the session runner resolves the kind from the WORKFLOW
    /// (<c>IWorkflowResolver.ResolveStartKind</c> over <c>state.workflowStepIndices</c> and the QA
    /// dials — a recorded mid-chain index resolves to Verify; a declared custom workflow's step 0 is
    /// whatever the author wrote, measured live as an Audit on a first launch this decision called a
    /// Deliver), and the loop RE-decides after this decision: a persisted completed HIGH-severity
    /// parallel audit becomes a queued fix before anything composes. Both rungs live here now —
    /// <paramref name="kinds"/> carries the same workflow resolver and QA policy the runner consults,
    /// and the ladder carries the parallel-audit fix — so the kind this decision names is the kind
    /// the dispatch records.</para>
    /// <para>Round 8, the branches that SCHEDULE: the loop does not stop where this decision used to.
    /// It performs the scheduling and RE-DECIDES inside the same run, so three "no session composes"
    /// answers were answering a different launch. The scheduling itself is a pure function of the
    /// plan and the saved state (<see cref="GateScheduling"/> — the one copy
    /// <see cref="GateOrchestrator.ScheduleGateOrAudit"/> executes), and one of its three outcomes,
    /// the auto-fix audit, is followed by that re-decision with NO subprocess in between: so the
    /// decision falls through the rungs below with the audit pending and carries
    /// <see cref="LaunchStep.ScheduleGateOrAudit"/> together with the Audit session it produces. The
    /// other two queue a FULL GATE BATTERY, and so does the completion branch — there the decision
    /// genuinely ends, because a battery's exit codes are subprocesses.</para>
    /// <para>Still deliberately unmodelled, because their outcome is NOT a pure function of the
    /// saved state: the gate batteries just named (a red REQUIRED gate queues a fix and the same run
    /// composes a Fix session — <see cref="LaunchDecision.Schedules"/> and the step tell a surface to
    /// say so), the pre-hook (a subprocess whose exit code decides), approval mode (the designed
    /// launch flow — the operator is present, and `conductor approve` is the next keystroke, not a
    /// failure), the DNS preflight (measures the network at spawn time), and the in-process
    /// backoffs (not persisted, so they cannot exist at launch). And one thing that IS modelled but
    /// only as a disclosure: a persisted <c>state.pendingParallelAudit</c> makes the launch spawn an
    /// audit LANE AGENT — real model spend — before the composed session
    /// (<see cref="LaunchDecision.SpawnsParallelAuditLane"/>); the lane's outcome and cost are not a
    /// function of the saved state, so a drill can only say that it will happen.</para></summary>
    /// <param name="nowUtc">The clock the <see cref="RunState.BlockedUntilUtc"/> comparison uses.
    /// Null means now; a test states an instant instead.</param>
    /// <param name="kinds">The collaborators the workflow rung of the kind ladder consults — the
    /// SAME resolver and QA policy the session runner will (the loop passes its own; a surface at
    /// rest constructs the defaults, which are what every host without a custom seam runs). Null
    /// means the defaults with no work graph, which resolves identically for every plan that does
    /// not set a per-item QA dial.</param>
    public static LaunchDecision NextAction(PlanConfig plan, RunState state, TrackerSnapshot track,
        DateTime? nowUtc = null, LaunchKindInputs? kinds = null)
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

        var decision = Decide(plan, state, track, kinds ?? LaunchKindInputs.Default);

        // An agent-declared wait (SC5.1) does not change WHAT runs next, only WHEN: the loop sleeps
        // at the session boundary until the window opens, then executes exactly this decision. Said
        // as an annotation rather than a step for that reason — and said at all because a surface
        // that names a session without naming the hours of sleep in front of it is lying by omission.
        return state.BlockedUntilUtc is { } blocked && (nowUtc ?? DateTime.UtcNow) < blocked
            ? decision with { SleepUntilUtc = blocked }
            : decision;
    }

    private static LaunchDecision Decide(PlanConfig plan, RunState state, TrackerSnapshot track, LaunchKindInputs kinds)
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

        // Round 8: this branch SCHEDULES and then the loop RE-DECIDES inside the same run, so
        // stopping here answered a different launch than the one that happens. What gets scheduled is
        // GateScheduling's pure branch over the plan and the saved state — the same function the loop
        // executes — and two of its three outcomes queue a FULL BATTERY, whose exit codes are
        // subprocesses: there the decision genuinely ends and the surfaces disclose the battery. The
        // third queues an auto-fix audit, and the very next decision (no subprocess in between)
        // composes that Audit session — so this one falls THROUGH the rungs below with the audit
        // pending, and the step keeps carrying the scheduling that produces it.
        var schedules = ScheduledWork.None;
        if (plan.PerPhaseGates && track.StageDone(stage.Id)
            && !state.ConfirmedStages.Contains(stage.Id)
            && fix == null && state.PendingResume == null
            && state.PendingVerify == null && state.PendingAudit == null)
        {
            schedules = GateScheduling.Classify(plan, state, stage.Id);
            if (schedules != ScheduledWork.AutoFixAudit)
                return new LaunchDecision(LaunchStep.ScheduleGateOrAudit, stage, stage.Id, SessionKind.Deliver,
                    Schedules: schedules);
        }

        // A queued audit — persisted, or about to be by the scheduling above — gets its turn
        // regardless of the attempt budget and takes the audit rung of the kind ladder.
        var audits = state.PendingAudit != null || schedules == ScheduledWork.AutoFixAudit;

        // The attempt budget. The loop resets the counter when it ENTERS a stage, so exhaustion is
        // only real on the stage the state is already standing in; a queued audit gets its turn
        // regardless — the loop's own guard. What the loop DOES on this branch is not pure — it
        // consults the advisor (a model call when one is configured) and otherwise parks at
        // NeedsHuman — which is exactly why a launch drill must model that the branch FIRES: the
        // session it would otherwise promise never composes, and `conductor resume` does not reset
        // the counter (only `conductor retry-stage` and `goto` do).
        if (stage.Id == state.CurrentStage && !audits
            && state.AttemptsThisStage >= MaxAttempts(plan, stage))
            return new LaunchDecision(LaunchStep.ExhaustedAttempts, stage, stage.Id, SessionKind.Deliver);

        if (plan.Limits.MaxSessions is { } liveCap && liveCap > 0 && state.SessionCounter >= liveCap)
            return new LaunchDecision(LaunchStep.SessionCap, stage, stage.Id, SessionKind.Deliver,
                Schedules: schedules);

        // Round 6: the loop consumes a completed HIGH-severity parallel audit BEFORE anything
        // composes (RunLoop's own branch, guarded by the same "no fix already queued") — the outcome
        // becomes a queued PendingFix and the turn goes around, so the session the launch composes
        // takes the fix's rung of the ladder. The flag tells the loop to perform that
        // materialization; the ladder below tells every surface what composes after it.
        var queuesAuditFix = fix == null
            && state.ParallelAuditOutcome is { Completed: true, MaxSeverity: AuditFindingSeverity.High };

        // The loop's own ladder (SessionRunner.ResolveSessionKind / PendingToKind): resume, audit,
        // VERIFY, fix, then the WORKFLOW's start kind — not a bare Deliver. Verify was missing here
        // until round 5; the workflow rung and the parallel-audit fix were missing until round 6,
        // measured live: a recorded mid-chain index spawned `Verify S1` where the drill said
        // Deliver, a custom workflow's first launch spawned `Audit S1`, and a persisted HIGH audit
        // outcome spawned `Fix S1` — wrong kind, wrong prompt, wrong measured argv, three times.
        var kind = state.PendingResume != null ? SessionKind.Resume
            : audits ? SessionKind.Audit
            : state.PendingVerify != null ? SessionKind.Verify
            : fix != null || queuesAuditFix ? SessionKind.Fix
            : WorkflowKind(plan, stage, state, track, kinds);

        // Round 6's rider: with a persisted pendingParallelAudit the launch's FIRST act — before the
        // composed session — is to spawn the audit lane agent, which is real model spend. Pure to
        // read (the same guard the loop's branch holds), impossible to price at rest; the decision
        // says THAT it happens so the drill can disclose it.
        var spawnsLane = state.PendingParallelAudit != null && fix == null && state.PendingResume == null;

        // The attempt the composed session announces. The loop resets AttemptsThisStage when it
        // ENTERS a stage — before every compose — so on a stage change the number is 1, whatever the
        // saved counter says about the stage being left. Carried on the decision (round 4) because
        // preflight rendered `attempt {saved+1}` off the un-entered state while the loop rendered
        // `attempt 1`, and the two measured prompts differed by exactly that.
        var attempt = (stage.Id == state.CurrentStage ? state.AttemptsThisStage : 0) + 1;
        return new LaunchDecision(
            schedules == ScheduledWork.AutoFixAudit ? LaunchStep.ScheduleGateOrAudit : LaunchStep.Compose,
            stage, stage.Id, kind, AttemptNumber: attempt,
            QueuesParallelAuditFix: queuesAuditFix, SpawnsParallelAuditLane: spawnsLane,
            Schedules: schedules);
    }

    /// <summary>The kind a session starts as when NOTHING is pending: the workflow's answer, exactly
    /// as the runner asks it — the QA dial of the item about to be claimed projects first (W4.4),
    /// the recorded step index is consumed without advancing, and a workflow fix with no failure
    /// context is honestly a delivery. The decision resolves on a COPY of the recorded indices with
    /// the stage-entry clear applied (a new stage starts its workflow over — the loop's own entry
    /// block does the same remove before the runner resolves), so nothing here mutates the state.
    /// The runner calls this same function with the live dictionary, whose recording side effect
    /// (a stage's very first resolution advances and records) belongs where the session begins.</summary>
    public static SessionKind WorkflowStartKind(PlanConfig plan, StageConfig stage, string itemQa,
        Dictionary<string, int> stepIndices, IWorkflowResolver workflows, IQaPolicy qa)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(stepIndices);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(qa);
        var workflow = workflows.Resolve(plan, stage, qa, itemQa);
        var kind = workflows.ResolveStartKind(workflow, stepIndices, stage.Id,
            qa.EffectiveSkipVerification(plan, stage, itemQa));
        // A fix without failure context is just a delivery attempt; fall back honestly — the
        // runner's own rule (it has no PendingFix to render a fix prompt from).
        return kind == SessionKind.Fix ? SessionKind.Deliver : kind;
    }

    private static SessionKind WorkflowKind(PlanConfig plan, StageConfig stage, RunState state,
        TrackerSnapshot track, LaunchKindInputs kinds)
    {
        var itemQa = ItemQa(track, stage, kinds.Graph?.Invoke());
        var indices = new Dictionary<string, int>(state.WorkflowStepIndices, StringComparer.Ordinal);
        if (stage.Id != state.CurrentStage) indices.Remove(stage.Id); // the loop's stage-entry clear
        return WorkflowStartKind(plan, stage, itemQa, indices, kinds.Workflows, kinds.Qa);
    }

    /// <summary>W4.4: the QA override of the item a session is about to claim — the first not-done
    /// checkpoint of the stage, which is exactly the item the assignment policy claims. Empty when
    /// the card has no override (the common case). One copy: <see cref="RunContext.ItemQaFor"/> and
    /// the runner both read it, so the drill and the dispatch project the same dial.</summary>
    public static string ItemQa(TrackerSnapshot? track, StageConfig stage, TaskGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (track == null || graph == null) return "";
        var itemId = track.ForStage(stage.Id).FirstOrDefault(c => c.IsOpen)?.Id;
        if (string.IsNullOrEmpty(itemId)) return "";
        return graph.Find(itemId)?.Qa ?? "";
    }
}
