using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Commands;

/// <summary>
/// KS3.4 — the compose leg on its own: what the next `conductor run`'s first turn does, decided and
/// composed by the run loop's own functions (<see cref="StageSelection.NextAction"/>,
/// <see cref="SessionComposer.Compose"/>) over the loop's own inputs as the loop prepares them.
/// Split from <c>PreflightCommand.Legs.cs</c> at round 6, when the leg grew the composer's tail and
/// the argv guard — one responsibility, one file, under the architecture ratchet's ceiling.
/// </summary>
public sealed partial class PreflightCommand
{
    // ───────────────────────────────────────────────────────────── compose

    /// <summary>The <c>run --dry-run</c> leg: what the next `conductor run`'s FIRST turn does —
    /// which is not always a session. Carries doctor's three prompt-side lints (<c>prompt</c>,
    /// <c>templates</c>, <c>argv</c>) because they answer the same question one stage earlier — will
    /// this compose at all, and will it fit in an argv.
    /// <para>Nothing is re-decided here, and — rounds 4 and 5's lesson — nothing is re-READ here
    /// either. The whole branch is <see cref="StageSelection.NextAction"/>, the run loop's OWN
    /// pre-compose sequence, called, not copied; and its two inputs are the loop's two inputs AS THE
    /// LOOP PREPARES THEM, because the loop mutates both before it reads them. The saved state is
    /// <see cref="JourneyCommand.PeekResumeAsync"/> (state.json, then the run.db row, read-only)
    /// with <see cref="CrashRecovery.Apply"/> on top AND — when that recovers nothing —
    /// <see cref="CrashRecovery.ApplyOrphan"/> over the same run.db, read-only, because the loop's
    /// startup recovery has a store-backed second half: an orphaned <c>SessionStarted</c> in the
    /// event log queues a Resume (or parks the run when the row carries no agent session id) before
    /// any session composes. The work is <see cref="WorkSnapshot.ReadAtRest"/>, which models the
    /// OTHER startup mutation: <c>RunLoop.RunAsync</c> syncs the declared plan into the work graph
    /// before its first read, so the drill projects the same sync
    /// (<see cref="Conductor.Core.Planning.WorkGraphSync.ProjectView"/>) over the graph at rest —
    /// rows declared since the last session are schedulable, retired rows are not, exactly as the
    /// launch will find them. Rounds 1–3 each removed a private copy of the DECISION; round 4
    /// removed a private copy of an INPUT; round 5 removed the private copy of the loop's own
    /// PRE-READ MUTATIONS; round 6 removed the private FAR SIDE — the kind ladder now ends in the
    /// workflow's start kind and the parallel-audit fix (<see cref="LaunchKindInputs"/> hands the
    /// decision the same resolver, QA policy and work graph the session runner consults), and the
    /// prompt is <see cref="SessionComposer"/>'s, the one composer the dispatch itself calls.</para></summary>
    internal static async Task<Leg> ComposeLegAsync(PlanConfig plan, IReadOnlyList<DoctorCommand.Check> checks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checks);
        var mine = checks.Where(c => CheckOwner.TryGetValue(c.Name, out var o) && o == ComposeLegName).ToList();

        // Unreachable from a loaded plan (PlanConfig.CollectErrors refuses an empty stages list);
        // kept for callers that construct a PlanConfig in memory.
        if (plan.Stages.Count == 0)
            return FromChecks(ComposeLegName, mine, "the plan declares no stages, so no session composes");

        var state = await JourneyCommand.PeekResumeAsync(plan).ConfigureAwait(false);
        // The loop's startup recovery, applied to the peeked copy (never written back): a crash's
        // persisted Running/VerifyingGates/Backoff becomes the queued Resume the loop will compose —
        // and when state.json remembers nothing, the event log gets the same question the loop asks
        // it (RunLoop.RecoverFromCrash's store-backed half), through the same shared transitions.
        var recovery = CrashRecovery.Apply(state);
        var orphan = recovery.Interrupted is null && state.PendingResume is null
            ? PeekOrphan(plan, state)
            : CrashRecovery.OrphanOutcome.Nothing;
        if (orphan.ParkedOrphanNumber is { } unresumable)
            return FromChecks(ComposeLegName, mine,
                $"run.db's event log holds an orphaned session #{unresumable} with no agent session id — the next " +
                "`conductor run` parks at NeedsHuman before spawning anything",
                ["the loop cannot resume a session the log never attributed — review the run, then `conductor resume` " +
                 "into it once the orphan is resolved"],
                "fail");
        var track = WorkSnapshot.ReadAtRest(plan, state.RunId, () => ReadDeclared(plan));
        // The work graph at rest, folded once — the kind ladder's per-item dial, the assignment
        // policy's path claims and the task-context section all read the same projection the runner
        // folds from its live store.
        var graph = SessionComposer.GraphAtRest(plan, state.RunId);
        var next = StageSelection.NextAction(plan, state, track,
            kinds: new LaunchKindInputs(new WorkflowEngine(), new DefaultQaPolicy(), () => graph));
        var leg = ComposeLegFor(plan, state, track, graph, next, mine);

        if (recovery.Interrupted is { } cut)
            leg = leg with
            {
                Detail = [.. leg.Detail,
                    $"session #{cut.Number} was killed mid-flight — `conductor run` recovers it at startup " +
                    "and queues a resume of its agent session"],
            };
        else if (orphan.Resumed is { } fromLog)
            leg = leg with
            {
                Detail = [.. leg.Detail,
                    $"run.db's event log shows session #{fromLog.Number} interrupted — `conductor run` recovers it " +
                    "at startup and queues a resume of its agent session"],
            };
        else if (recovery.ContinuedAborted)
            leg = leg with
            {
                Detail = [.. leg.Detail,
                    "the saved status is Aborted — `conductor run` continues the run " +
                    "(abort again with `conductor abort` if that was not the intent)"],
            };

        // The agent-declared wait in front of the decision (SC5.1), whatever the decision was: the
        // loop sleeps at the session boundary until the window opens and only then does what the
        // headline says. Not a failure — launching into a declared wait is the wait working — but a
        // drill that names the session without the hours of sleep in front of it understates launch.
        if (next.SleepUntilUtc is { } wakes)
            leg = leg with { Detail = [.. leg.Detail, SleepNote(state, wakes)] };
        return leg;
    }

    /// <summary>The sentence for each of the loop's branches. Split from the async shell so the
    /// sleep annotation above applies to every branch, not just the one whose case remembered it.</summary>
    private static Leg ComposeLegFor(PlanConfig plan, RunState state, TrackerSnapshot track,
        Conductor.Core.Events.TaskGraph? graph, LaunchDecision next, IReadOnlyList<DoctorCommand.Check> mine)
    {
        switch (next.Step)
        {
            case LaunchStep.ParkedStatus:
            {
                // The persisted residue of an escalation: the loop idles on Paused / NeedsHuman /
                // AwaitingOwner at 800ms polls, forever, before it reads the tracker or composes
                // anything — and RecoverFromCrash resets only a crash's statuses, never these. The
                // verb that continues this run is `conductor resume`, and a drill that says
                // "Launch with conductor run" here is prescribing an idle loop.
                var detail = new List<string>();
                if (state.AttentionReason is { Length: > 0 } why) detail.Add($"parked because: {why}");
                detail.Add("resolve what parked it, then `conductor resume` into the existing run — " +
                           "`conductor run` never lifts this status, it idles at the session boundary until something else does");
                return FromChecks(ComposeLegName, mine,
                    $"the saved run is parked — state.json says status {state.Status} — the next " +
                    "`conductor run` idles at the session boundary and spawns nothing",
                    detail, "fail");
            }

            case LaunchStep.EmptyTracker:
                return FromChecks(ComposeLegName, mine,
                    $"{plan.Tracker} has no parseable checkpoint rows — `conductor run` parks at NeedsHuman before spawning anything",
                    ["check the table format — the loop reads rows of `| id | title | status | … |`"], "fail");

            // Round 7's blocking findings (2) and (3), and the same lesson twice: the launch's next
            // act on both of these branches is a FULL GATE BATTERY, and the loop RE-DECIDES on its
            // result — a red required gate queues a fix and the same run composes a Fix session. The
            // battery is subprocesses, so the drill genuinely cannot say which way it goes; what it
            // must not do is assert the negative ("no session composes", "rather than spawning a
            // session") for a launch whose next turn can spawn one. The headline names what runs
            // first; the detail names both outcomes.
            case LaunchStep.PhaseGate when EmptyBattery(plan):
                return FromChecks(ComposeLegName, mine,
                    $"the next `conductor run` runs the queued full-battery phase gate for stage '{next.StageId}' " +
                    "— the plan declares no gates, so that battery is empty and confirms the stage",
                    [EmptyBatteryNote, AfterAConfirmation]);

            case LaunchStep.PhaseGate:
                return FromChecks(ComposeLegName, mine,
                    $"the next `conductor run` runs the queued full-battery phase gate for stage '{next.StageId}' " +
                    "BEFORE anything composes — what follows depends on the gates",
                    BatteryOutcomes(next.StageId ?? state.CurrentStage, "phase gate",
                        "green confirms the stage, and " + AfterAConfirmation));

            case LaunchStep.ConfirmCompletion when EmptyBattery(plan):
                return FromChecks(ComposeLegName, mine,
                    "every stage reads done — the next `conductor run` confirms completion rather than spawning a session",
                    [EmptyBatteryNote]);

            case LaunchStep.ConfirmCompletion:
                return FromChecks(ComposeLegName, mine,
                    "every stage reads done — the next `conductor run` runs the completion battery BEFORE closing " +
                    "the plan; what follows depends on the gates",
                    BatteryOutcomes(state.CurrentStage, "completion battery",
                        "green closes the plan and spawns nothing"));

            case LaunchStep.NothingRunnable:
                // RunLoop's own answer to this is NeedsHuman before a session: the run starts, parks
                // and spends nothing. That is a launch failure, and only this leg can see it coming.
                return FromChecks(ComposeLegName, mine,
                    "no stage is runnable — every stage left is skipped, or blocked by a `dependsOn` that is neither done nor skipped",
                    ["`conductor run` would park at NeedsHuman before spawning anything — review the dependsOn chain " +
                     "and state.skippedStages"],
                    "fail");

            case LaunchStep.HandoffEscalation:
                // Truthful, not red: the escalation leg owns this failure and fails on the same
                // tracker read, so the drill still names exactly one leg.
                return FromChecks(ComposeLegName, mine,
                    "the next `conductor run` parks at NeedsHuman — the tracker handoff asks for a human — no session composes",
                    ["see the escalation leg"]);

            // Round 7's blocking finding (1): the third scheduling outcome — the auto-fix audit — is
            // NOT a stopping point. The loop queues it and re-decides in the same run with no
            // subprocess in between, and that decision composes an Audit session. It is a pure
            // function of the saved state, so it is modelled rather than disclosed: the guard sends
            // that branch past this switch into the compose path below, which projects the same
            // scheduling onto the peeked state and composes the session the launch will spawn.
            case LaunchStep.ScheduleGateOrAudit when next.Schedules != ScheduledWork.AutoFixAudit && EmptyBattery(plan):
                return FromChecks(ComposeLegName, mine,
                    $"stage '{next.StageId}' checkpoints all read DONE but the stage is unconfirmed — the next " +
                    $"`conductor run` schedules {GateScheduling.Describe(next.Schedules)} — the plan declares no " +
                    "gates, so that battery is empty and confirms the stage",
                    [EmptyBatteryNote, AfterAConfirmation]);

            case LaunchStep.ScheduleGateOrAudit when next.Schedules != ScheduledWork.AutoFixAudit:
                return FromChecks(ComposeLegName, mine,
                    $"stage '{next.StageId}' checkpoints all read DONE but the stage is unconfirmed — the next " +
                    $"`conductor run` schedules {GateScheduling.Describe(next.Schedules)} and runs that battery " +
                    "BEFORE anything composes — what follows depends on the gates",
                    BatteryOutcomes(next.StageId, "phase gate",
                        "green confirms the stage" +
                        (next.Schedules == ScheduledWork.PhaseGateThenParallelAudit
                            ? " and hands its audit to a parallel lane agent (real model spend) beside the next stage's session"
                            : "") +
                        ", and " + AfterAConfirmation));

            case LaunchStep.ExhaustedAttempts:
            {
                // The loop's escalation branch fires BEFORE its compose branch, so the session this
                // leg would otherwise promise never composes: with no advisor configured the run
                // parks at NeedsHuman deterministically, and with one configured the "launch" the
                // READY line prescribes starts with a model call. Reachable at launch precisely
                // because `conductor resume` does not reset the counter — only `retry-stage`/`goto` do.
                var budget = StageSelection.MaxAttempts(plan, next.Stage!);
                return FromChecks(ComposeLegName, mine,
                    $"stage '{next.StageId}' has used all {budget} attempts ({state.AttemptsThisStage}/{budget}) — " +
                    "the next `conductor run` escalates instead of composing — no session composes",
                    ["with no advisor configured the run parks at NeedsHuman before spawning anything; with one, " +
                     "its first act is a model call",
                     "grant a fresh budget with `conductor retry-stage` (resets the counter — `conductor resume` " +
                     "does not), raise limits.stageSlackFactor, or `conductor skip` the stage"],
                    "fail");
            }

            case LaunchStep.SessionCap:
                return FromChecks(ComposeLegName, mine,
                    $"session cap reached ({state.SessionCounter}/{plan.Limits.MaxSessions}) — the next `conductor run` " +
                    "parks at the session boundary — no session composes",
                    ["raise or clear limits.maxSessions (`conductor plan set limits.maxSessions <n>`, or the Plan tab) " +
                     "before launching, or launch deliberately parked"],
                    "fail");
        }

        var kind = next.Kind;
        try
        {
            // The stage-entry field mutations the loop performs before it composes (start head,
            // attempt counter, the fix that does not survive its stage, the recorded workflow
            // index), applied to the PEEKED copy — never written back. Round 6: an audit composed
            // at rest rendered "HEAD~1" where the launch rendered the entry head.
            SessionComposer.ProjectStageEntry(state, next.Stage!, Git.Head(plan.Repo));
            // Round 8: and the OTHER mutation the loop performs before this compose — on the
            // scheduling branch its first act is to queue the auto-fix audit, then re-decide, and
            // that decision (the one this leg is rendering) composes the Audit session from the
            // pending it just queued. Same order the loop uses: stage entry, then the scheduling.
            if (next.Step == LaunchStep.ScheduleGateOrAudit)
                GateScheduling.Project(plan, state, next.Stage!.Id, state.CurrentStageStartHead ?? Git.Head(plan.Repo));
            var composed = SessionComposer.Compose(plan, new PromptBuilder(plan), new DefaultAssignmentPolicy(),
                state, track, graph, store: null,
                kind, next.Stage!, state.SessionCounter + 1, next.AttemptNumber,
                state.PendingResume, state.PendingAudit, state.PendingVerify, state.PendingFix);
            var detail = new List<string>();
            if (next.Schedules == ScheduledWork.AutoFixAudit)
                detail.Add($"stage '{next.Stage!.Id}' checkpoints all read DONE but the stage is unconfirmed — the " +
                           "launch's first turn schedules the auto-fix audit (a single confirming battery runs " +
                           "after it), and the session named above is the one that scheduling composes");
            if (next.QueuesParallelAuditFix)
                detail.Add("state.parallelAuditOutcome holds completed HIGH-severity findings — the launch's first " +
                           "turn queues the fix composed here before anything spawns");
            if (composed.ConsumesParallelAuditOutcome)
                detail.Add("the prompt carries the completed parallel audit's LOW/MEDIUM findings " +
                           "(## Parallel audit findings) — the launch consumes them with this session");
            if (next.SpawnsParallelAuditLane)
                detail.Add($"state.pendingParallelAudit is queued for stage '{state.PendingParallelAudit!.StageId}' — " +
                           "the launch's FIRST act spawns that read-only audit lane agent (real model spend, in a " +
                           "detached worktree) before the session named above; the drill can state that it happens " +
                           "but cannot model what the lane will spend or find");
            // Direction stated at the drill (round 6, item 4): one value in the prompt is assigned
            // AT LAUNCH and cannot be composed at rest — ToolContract embeds the supervising
            // conductor's pid (SF0.3, so an agent can identify its own supervisor before killing a
            // pid). The drill measures with its own pid; the launch renders its own. Saying so is
            // honest; a silently wrong count is the defect.
            detail.Add($"the prompt embeds the supervising conductor's pid (ToolContract) — assigned at launch, " +
                       $"measured here with this drill's ({Environment.ProcessId}) — so the spawned length can " +
                       "differ by the pid-width difference (single characters)");
            detail.AddRange(KnowledgeBatteryCaveat(plan, state, composed));
            var argv = ArgvGuard(plan, state, composed);
            detail.AddRange(argv.Detail);
            return FromChecks(ComposeLegName, mine,
                $"next session #{state.SessionCounter + 1} is {composed.Kind} on stage '{composed.Stage.Id}', " +
                $"composing to {composed.Prompt.Length} chars (nothing spawned)",
                detail, argv.State);
        }
        catch (PromptCompositionException ex)
        {
            // Doctor's own prompt lint usually names the same template first; saying it twice under
            // one leg is noise, so the refusal is only spelled out when nothing else already did.
            var already = mine.Any(c => c.State == "fail");
            return FromChecks(ComposeLegName, mine,
                $"the prompt for the next session ({kind} on stage '{next.Stage!.Id}') is REFUSED — nothing would spawn",
                already ? [] : [ex.Message], "fail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FromChecks(ComposeLegName, mine,
                $"the prompt for the next session ({kind} on stage '{next.Stage!.Id}') could not be read",
                [ex.Message], "warn");
        }
    }

    /// <summary>Both ways a gate battery the drill may not run can go — round 7's finding (2) and (3),
    /// which is the one shape of dishonesty this leg can still commit: asserting a definite negative
    /// about a launch whose next act is a battery. A red REQUIRED gate queues a fix and the SAME run
    /// re-decides into a Fix session (<c>VerdictEngine.ConfirmCompletionAsync</c>,
    /// <c>RunPhaseGateAsync</c>, then <c>RunLoop</c>'s <c>continue</c>), so a drill that promised "no
    /// session composes" was measurably wrong. Exit codes are subprocesses — not a function of the
    /// saved state — so the honest answer names both outcomes and picks neither.</summary>
    /// <summary>Whether the battery on this branch can go red AT ALL. A plan that declares no gates
    /// and no setup/teardown hook has an EMPTY battery, and <c>GateRunner.AllRequiredPassed([])</c> is
    /// true — a pure function of the plan, so the drill states the outcome instead of naming both.
    /// (The hooks are in the guard because <c>RunBatteryAsync</c> runs them around the gates, so a
    /// declared hook is execution this drill has not measured either.)</summary>
    private static bool EmptyBattery(PlanConfig plan)
        => plan.Gates.Count == 0 && plan.Setup is null && plan.Teardown is null;

    private const string EmptyBatteryNote =
        "the plan declares no gates and no setup/teardown hook, so this battery has nothing to run and passes by " +
        "construction — the red branch (a queued fix, a Fix session in this same run) cannot fire";

    private const string AfterAConfirmation =
        "the same `conductor run` carries straight on after a confirmation — the next stage's session, or the " +
        "completion battery when there is no next stage — unless it was launched with --once";

    private static IReadOnlyList<string> BatteryOutcomes(string? stageId, string what, string green)
        =>
        [
            $"a red REQUIRED gate queues a fix and the SAME `conductor run` composes a Fix session on stage " +
            $"'{stageId}' — no second launch, no second drill",
            green,
            $"the {what}'s exit codes are subprocesses, so this drill can name both outcomes but not pick one; " +
            "`conductor gate --full` runs the battery itself if you want the answer before launching",
        ];

    /// <summary>What the measured length does NOT include, said as a number rather than as a hedge.
    /// Everything else IS measured now — template, batteries at rest, the claimed-items list, the
    /// task-context cards, the parallel-audit findings (round 6's missing tail) — through the same
    /// <see cref="SessionComposer"/> call the dispatch makes. The one remainder is the store-backed
    /// knowledge batteries (the ledger, the run's open bugs), which render from the LIVE store at
    /// spawn; the WHOLE battery section is capped at <c>batteries.maxBytes</c> (2048 by default) by
    /// <c>BatteryGroup.Render</c>, plus at most two characters of truncation tail, so the spawned
    /// argv is at most the battery-less composition plus that cap — a TRUE ceiling, derived from
    /// <see cref="SessionComposer.Composition.PromptSansBattery"/> because adding the cap to a string
    /// already carrying measured batteries would count them twice.
    /// <para>Silent on a fresh run, on a plan whose store does not exist yet, and when both knowledge
    /// batteries are switched off: there is nothing unmeasured to warn about.</para></summary>
    private static IReadOnlyList<string> KnowledgeBatteryCaveat(PlanConfig plan, RunState state, SessionComposer.Composition composed)
    {
        var cfg = plan.Batteries;
        var knowledgeOn = (cfg?.Ledger ?? true) || (cfg?.Bugs ?? true);
        if (!knowledgeOn || state.RunId.Length == 0 || !File.Exists(plan.RunDbPath)) return [];

        var maxBytes = cfg?.MaxBytes ?? 2048;
        return
        [
            $"the ledger and open-bug batteries render from the live store when the session spawns; the drill " +
            $"reads run.db read-only and measures everything else the launch appends, and the whole battery " +
            $"section is capped — so the spawned argv is at most {BatteryCeiling(plan, composed)} chars " +
            $"(batteries.maxBytes {maxBytes})",
        ];
    }

    /// <summary>The true upper bound on the spawned prompt: the battery-less composition plus the
    /// whole battery section's cap and its joins, plus the width the launch's pid can add over the
    /// drill's own (ToolContract embeds it; a Windows pid is at most ten digits).</summary>
    internal static int BatteryCeiling(PlanConfig plan, SessionComposer.Composition composed)
        => composed.PromptSansBattery.TrimEnd().Length + 2 + (plan.Batteries?.MaxBytes ?? 2048) + 2 + PidSlack;

    /// <summary>How much wider than this process's the launch's pid can render.</summary>
    internal static int PidSlack
        => Math.Max(0, 10 - Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);

    /// <summary>The 8191-char guard, on the argv that would ACTUALLY spawn — round 6 measured a
    /// drill reporting 7592 chars for a launch that spawned 10094, past the cmd.exe ceiling this
    /// very leg carries doctor's <c>argv</c> check to guard, because the sections outside
    /// <c>batteries.maxBytes</c> were composed but never measured. The composed prompt is put
    /// through the same substitution and quoting the spawn uses (<c>AgentSession.ResolveArgs</c>,
    /// <c>ProcessStartInfo.ArgumentList</c> rules) against the ceiling this machine will actually
    /// hit (<see cref="DoctorCommand.ArgvCeiling"/>). Fails when the measured argv is over; warns
    /// when only the unmeasured-battery ceiling could put it over.</summary>
    private static (string State, IReadOnlyList<string> Detail) ArgvGuard(PlanConfig plan, RunState state,
        SessionComposer.Composition composed)
    {
        var agent = plan.ResolveAgent(composed.Stage);
        if (composed.Assignment.Model is { Length: > 0 } model) agent.Model = model;
        if (composed.Assignment.Command is { Length: > 0 } command) agent.Command = command;
        var template = composed.Kind == SessionKind.Resume && agent.ResumeArgs is { Count: > 0 } resume
            ? resume : agent.Args;
        if (template.Count == 0) return ("ok", []);

        const string probeId = "00000000-0000-0000-0000-000000000000";
        var argv = AgentSession.ResolveArgs(template, composed.Prompt, probeId,
            composed.Kind == SessionKind.Resume ? probeId : null, agent.Model);
        var length = DoctorCommand.CommandLineLength(agent.Command, argv);
        var (ceiling, why) = DoctorCommand.ArgvCeiling(plan);
        if (length > ceiling)
            return ("fail",
            [
                $"the composed argv for this session is {length} chars against the {ceiling}-char ceiling ({why}) — " +
                "the agent is truncated or refused at spawn while the run scores the session as if it had read " +
                "everything; shorten promptExtra/packs/stage notes, or the sections this session appends " +
                "(claimed items, task context, parallel-audit findings)",
            ]);

        // Bug #21's rule, doctor's own: clearing CreateProcess' ceiling is not clearing the ceiling
        // — the same session is fatal the moment agent.command lands on a .cmd/.bat shim, which is
        // what an npm install of an agent CLI is on Windows.
        if (ceiling > DoctorCommand.CmdExeCommandLineCeiling && length > DoctorCommand.CmdExeCommandLineCeiling)
            return ("warn",
            [
                $"the composed argv is {length} chars against the {ceiling}-char ceiling ({why}), but over the " +
                $"{DoctorCommand.CmdExeCommandLineCeiling}-char cmd.exe ceiling — this session is fatal on any machine " +
                "whose agent.command resolves to a .cmd/.bat shim (an npm-installed CLI is exactly that)",
            ]);

        // The two launch-time remainders can only add so much: the store-backed batteries up to
        // their cap, and the supervising pid up to its full width (a Windows pid is at most ten
        // digits; the drill measured with its own). If THAT bound crosses the ceiling, the launch
        // may truncate even though the measured argv clears it.
        var caveatApplies = ((plan.Batteries?.Ledger ?? true) || (plan.Batteries?.Bugs ?? true))
            && state.RunId.Length > 0 && File.Exists(plan.RunDbPath);
        var bound = caveatApplies
            ? length - composed.Prompt.Length + BatteryCeiling(plan, composed)
            : length + PidSlack;
        if (bound > ceiling)
            return ("warn",
            [
                $"the composed argv is {length} chars, but with the launch-time remainders (the store-backed " +
                $"batteries' cap, the pid width) it can reach {bound} against the {ceiling}-char ceiling ({why}) — " +
                "trim batteries.maxBytes or the prompt",
            ]);
        return ("ok", []);
    }

    /// <summary>The declared wait, as one sentence with the timestamp and — when the session that
    /// declared it said why — the reason, in that session's own words.</summary>
    private static string SleepNote(RunState state, DateTime wakes)
        => $"state.blockedUntilUtc {wakes:yyyy-MM-dd HH:mm:ss}Z is still in the future — the loop sleeps at the " +
           "session boundary until then before doing any of this" +
           (state.BlockedReason is { Length: > 0 } why ? $" ({why})" : "");

    /// <summary>The DECLARED snapshot — the row set and the handoff block — handed to
    /// <see cref="WorkSnapshot.ReadAtRest"/> RAW, allowed to throw: the at-rest reader mirrors the
    /// live sync, which skips on an unreadable declaration, so it must see the failure itself
    /// rather than an empty snapshot it cannot tell from a deliberately empty one. Never the
    /// scheduling input on its own: the statuses that decide are the graph's (round 4), and the row
    /// set that decides is the declaration's as the startup sync projects it (round 5).</summary>
    private static TrackerSnapshot ReadDeclared(PlanConfig plan)
        => ProgressProviderFactory.Create(plan).Read(plan, CancellationToken.None);

    /// <summary>The store-backed half of startup recovery, asked of the run.db AT REST — the same
    /// question <c>RunLoop.RecoverFromCrash</c> asks a live store, through the same
    /// <see cref="CrashRecovery.ApplyOrphan"/> transitions, applied to the peeked copy and never
    /// written back. A missing or unanswerable store recovers nothing, exactly as a run with no
    /// history has nothing to recover.</summary>
    private static CrashRecovery.OrphanOutcome PeekOrphan(PlanConfig plan, RunState state)
    {
        if (state.RunId.Length == 0 || !File.Exists(plan.RunDbPath)) return CrashRecovery.OrphanOutcome.Nothing;
        try
        {
            using var store = Conductor.Core.Store.SqliteRunStore.OpenReadOnly(plan.RunDbPath);
            return CrashRecovery.ApplyOrphan(state, store);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return CrashRecovery.OrphanOutcome.Nothing;
        }
    }
}
