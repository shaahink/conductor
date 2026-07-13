using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// Gate battery execution, phase-gate confirmation, and gate-result persistence,
/// extracted from Orchestrator (F7). All mutable state lives on the passed <see cref="RunState"/>
/// and <see cref="PlanConfig"/>; this class is pure execution logic over shared state.
/// </summary>
public sealed class GateOrchestrator(PlanConfig plan, RunState state, IEventSink events, IRunStore? store)
{
    public async Task<IReadOnlyList<GateResult>> RunBatteryAsync(
        Action<string> log,
        Action<string, string?> logWithOutcome,
        Action<IReadOnlyList<GateProgress>> onGates,
        CancellationToken ct,
        bool fastOnly)
    {
        await GateRunner.RunHookAsync(plan, plan.Setup, "setup", log, ct).ConfigureAwait(false);
        var stage = plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage);
        var headSha = Git.Head(plan.Repo);
        var gates = await GateRunner.RunAllAsync(plan, log, ct, fastOnly,
            state.CurrentStage, stage?.Kind, onGates,
            store, state.RunId, headSha).ConfigureAwait(false);
        await GateRunner.RunHookAsync(plan, plan.Teardown, "teardown", log, ct).ConfigureAwait(false);
        foreach (var g in gates)
        {
            var outcome = g.Cached ? "cached" : g.Skipped ? "skip" : g.Passed ? "pass" : g.Optional ? "warn" : "fail";
            logWithOutcome($"gate {g.Name}: {(g.Cached ? "CACHED" : g.Skipped ? "SKIP" : g.Passed ? "PASS" : g.Optional ? "WARN" : "FAIL")} ({g.Duration.TotalSeconds:0}s)", outcome);
        }
        return gates;
    }

    /// <summary>Persist gate results to the event log and run.db.</summary>
    public void PersistGates(IReadOnlyList<GateResult> gates, string scope, string? sessionId = null)
    {
        var sha = Git.Head(plan.Repo);
        foreach (var g in gates)
        {
            events.Emit(new GateFinished
            {
                SessionId = sessionId,
                Name = g.Name,
                Passed = g.Passed,
                Skipped = g.Skipped,
                Optional = g.Optional,
                ExitCode = g.ExitCode,
                DurationMs = (long)g.Duration.TotalMilliseconds,
                Scope = scope,
            });
            var tier = plan.Gates.FirstOrDefault(gc => gc.Name == g.Name)?.Tier ?? "full";
            store?.RecordGate(state.RunId,
                int.TryParse(sessionId, out var sn) ? sn : null,
                state.CurrentStage, g.Name, tier, scope, sha,
                g.Passed, g.Skipped, g.Optional, g.ExitCode,
                (long)g.Duration.TotalMilliseconds,
                g.Tail.Length > 2000 ? g.Tail[^2000..] : g.Tail);
        }
    }

    /// <summary>PerPhase: has the stage been reached (gate+audit confirmed)? Used by SelectStage.</summary>
    public bool IsStageComplete(string stageId, bool isPerPhase, Func<string, bool> trackStageDone)
        => isPerPhase ? state.ConfirmedStages.Contains(stageId) : trackStageDone(stageId);

    /// <summary>Schedule the audit or confirming battery for a stage whose checkpoints are all DONE.</summary>
    public void ScheduleGateOrAudit(string stageId, string startHead, Action<string> log, Func<string, bool> hasNextUnconfirmed)
    {
        state.StageStartHeads[stageId] = startHead;
        if (plan.Audit is { Enabled: true, EnableParallel: true } && !state.AuditedStages.Contains(stageId)
            && hasNextUnconfirmed(stageId))
        {
            state.PendingPhaseGate = new PendingPhaseGate { StageId = stageId, StageStartHead = startHead };
            state.PendingAudit = null;
            log($"stage {stageId} checkpoints all DONE — scheduling full-battery phase gate (parallel audit will follow)");
        }
        else if (plan.Audit is { Enabled: true } && !state.AuditedStages.Contains(stageId))
        {
            state.PendingAudit = new PendingAudit { StageId = stageId, StageStartHead = startHead };
            state.PendingPhaseGate = null;
            log($"stage {stageId} checkpoints all DONE — scheduling auto-fix audit (single confirming battery runs after it)");
        }
        else
        {
            state.PendingPhaseGate = new PendingPhaseGate { StageId = stageId, StageStartHead = startHead };
            log($"stage {stageId} checkpoints all DONE — scheduling full-battery phase gate");
        }
    }
}
