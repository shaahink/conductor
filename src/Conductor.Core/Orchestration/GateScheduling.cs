using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>What the <see cref="LaunchStep.ScheduleGateOrAudit"/> step queues on a stage whose rows
/// all read done while the stage is unconfirmed. Named as data because the choice is a pure function
/// of the plan and the saved state (round 8): the launch performs it and then RE-DECIDES inside the
/// same <c>conductor run</c>, so a surface that stops at "schedules something" cannot say whether a
/// session follows — and on one of these three branches, one does.</summary>
public enum ScheduledWork
{
    /// <summary>No scheduling on this decision.</summary>
    None,
    /// <summary>A <c>pendingAudit</c> — and the very next decision, in the same run with no
    /// subprocess in between, composes that Audit session.</summary>
    AutoFixAudit,
    /// <summary>A <c>pendingPhaseGate</c> — the launch's next act is a FULL gate battery, whose exit
    /// codes are subprocesses, so what follows it is not a function of the saved state.</summary>
    PhaseGate,
    /// <summary>A <c>pendingPhaseGate</c> whose green result confirms the stage and hands the audit
    /// to a parallel lane instead of a session.</summary>
    PhaseGateThenParallelAudit,
}

/// <summary>The scheduling branch of <see cref="GateOrchestrator.ScheduleGateOrAudit"/>, as one
/// shared function over the plan and the saved state — the copy the run loop EXECUTES and the copy
/// <see cref="StageSelection"/> DECIDES with are the same one, for the reason rounds 1–7 keep
/// re-teaching: a second copy of a decision is a second answer.
/// <para><see cref="Classify"/> only reads; <see cref="Project"/> applies it to a state (the loop's
/// live one, or a surface's peeked copy — the <see cref="SessionComposer.ProjectStageEntry"/>
/// pattern). <see cref="Narrate"/> is the loop's own log line, so a drill quoting the branch quotes
/// what the launch will print.</para></summary>
public static class GateScheduling
{
    /// <summary>Is there a later stage this run has neither confirmed nor skipped? The parallel audit
    /// only makes sense when there is a next deliver to run beside it — <c>VerdictEngine</c>'s own
    /// question, asked of the same two lists.</summary>
    public static bool HasNextUnconfirmedStage(PlanConfig plan, RunState state, string stageId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        var idx = plan.Stages.FindIndex(s => s.Id == stageId);
        if (idx < 0) return false;
        for (var i = idx + 1; i < plan.Stages.Count; i++)
        {
            var sid = plan.Stages[i].Id;
            if (!state.SkippedStages.Contains(sid) && !state.ConfirmedStages.Contains(sid))
                return true;
        }
        return false;
    }

    /// <summary>Which of the three this stage gets, read-only.</summary>
    public static ScheduledWork Classify(PlanConfig plan, RunState state, string stageId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        if (plan.Audit is { Enabled: true, EnableParallel: true } && !state.AuditedStages.Contains(stageId)
            && HasNextUnconfirmedStage(plan, state, stageId))
            return ScheduledWork.PhaseGateThenParallelAudit;
        if (plan.Audit is { Enabled: true } && !state.AuditedStages.Contains(stageId))
            return ScheduledWork.AutoFixAudit;
        return ScheduledWork.PhaseGate;
    }

    /// <summary>Apply the branch to <paramref name="state"/> — the queued pending and the recorded
    /// start head — and say which one fired. The loop calls this on its live state and saves; a
    /// surface at rest calls it on a peeked copy it never writes back, exactly as it projects the
    /// stage entry, so the session it composes afterwards is composed from the same fields the
    /// launch will hold when it composes.</summary>
    public static ScheduledWork Project(PlanConfig plan, RunState state, string stageId, string startHead)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        var work = Classify(plan, state, stageId);
        state.StageStartHeads[stageId] = startHead;
        switch (work)
        {
            case ScheduledWork.PhaseGateThenParallelAudit:
                state.PendingPhaseGate = new PendingPhaseGate { StageId = stageId, StageStartHead = startHead };
                state.PendingAudit = null;
                break;
            case ScheduledWork.AutoFixAudit:
                state.PendingAudit = new PendingAudit { StageId = stageId, StageStartHead = startHead };
                state.PendingPhaseGate = null;
                break;
            default:
                state.PendingPhaseGate = new PendingPhaseGate { StageId = stageId, StageStartHead = startHead };
                break;
        }
        return work;
    }

    /// <summary>What gets queued, as a noun phrase, for surfaces that build their own sentence around
    /// it (the preview's line, the drill's headline) — so all three name the same thing.</summary>
    public static string Describe(ScheduledWork work) => work switch
    {
        ScheduledWork.AutoFixAudit => "the auto-fix audit session",
        ScheduledWork.PhaseGateThenParallelAudit => "the full-battery phase gate (a parallel audit lane follows it)",
        _ => "the full-battery phase gate",
    };

    /// <summary>The run loop's own sentence for the branch — unchanged wording, in one place.</summary>
    public static string Narrate(ScheduledWork work, string stageId) => work switch
    {
        ScheduledWork.PhaseGateThenParallelAudit =>
            $"stage {stageId} checkpoints all DONE — scheduling full-battery phase gate (parallel audit will follow)",
        ScheduledWork.AutoFixAudit =>
            $"stage {stageId} checkpoints all DONE — scheduling auto-fix audit (single confirming battery runs after it)",
        _ => $"stage {stageId} checkpoints all DONE — scheduling full-battery phase gate",
    };
}
