using Conductor.Core.Events;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Core.Orchestration;

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
/// loop's precedence: resume, then audit, then verify, then fix, then delivery — and so is
/// <paramref name="AttemptNumber"/>: the attempt the composed session announces, already accounting
/// for the counter reset the loop performs on stage ENTRY, so every renderer of this decision (the
/// live session, the dry run, preflight's compose leg) prints the same <c>attempt n/m</c>.
/// <paramref name="SleepUntilUtc"/> is the agent-declared wait in front of the decision, when one is
/// saved and still in the future: the loop sleeps at the session boundary until then, and only then
/// does what the rest of this record says.
/// <para>Round 6's two flags, meaningful only for <see cref="LaunchStep.Compose"/>:
/// <paramref name="QueuesParallelAuditFix"/> — the launch's first turn materializes the completed
/// HIGH-severity parallel audit into the queued fix (<see cref="SessionComposer.FixFromParallelAudit"/>)
/// and goes around, so <paramref name="Kind"/> already accounts for it;
/// <paramref name="SpawnsParallelAuditLane"/> — the launch spawns the queued parallel-audit LANE
/// AGENT (real model spend) before the composed session, which a drill can disclose but not price.</para></summary>
public sealed record LaunchDecision(LaunchStep Step, StageConfig? Stage, string? StageId, SessionKind Kind,
    DateTime? SleepUntilUtc = null, int AttemptNumber = 1,
    bool QueuesParallelAuditFix = false, bool SpawnsParallelAuditLane = false);

/// <summary>The collaborators <see cref="StageSelection.NextAction"/>'s workflow rung consults —
/// stated as inputs because the kind is only shared if the INPUTS are shared (rounds 4 and 5's
/// lesson, applied to round 6's rung): the loop passes the same resolver and QA policy its session
/// runner will consult, and <paramref name="Graph"/> supplies the work graph the per-item QA dial
/// reads (invoked only when the rung is actually consulted; null means no graph, which projects
/// identically for every item without a dial).</summary>
public sealed record LaunchKindInputs(IWorkflowResolver Workflows, IQaPolicy Qa, Func<TaskGraph?>? Graph = null)
{
    /// <summary>What every host without a custom seam runs: the engine's own workflow resolver and
    /// QA policy, no graph. <see cref="RunContext"/> constructs exactly these when none are
    /// injected, which is why a caller with no seam of its own may use this default honestly.</summary>
    public static LaunchKindInputs Default { get; } = new(new WorkflowEngine(), new DefaultQaPolicy());
}
