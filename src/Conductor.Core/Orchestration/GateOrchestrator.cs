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
    private PlanConfig _plan = plan;

    /// <summary>G3.2 live plan reload: point the gate battery at the freshly loaded _plan. Only called
    /// from the run loop at a session boundary (never while gates are running).</summary>
    public void SwapPlan(PlanConfig fresh) => _plan = fresh;

    public async Task<IReadOnlyList<GateResult>> RunBatteryAsync(
        Action<string> log,
        Action<string, string?> logWithOutcome,
        Action<IReadOnlyList<GateProgress>> onGates,
        CancellationToken ct,
        bool fastOnly)
    {
        await GateRunner.RunHookAsync(_plan, _plan.Setup, "setup", log, ct).ConfigureAwait(false);
        var stage = _plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage);
        var headSha = Git.Head(_plan.Repo);
        // KS4.1: includeHoldout: true, and this is the ONLY place in the engine that passes it. This
        // method is reached from VerdictEngine alone — the per-session battery, the phase gate and
        // the closing battery — which is exactly "run only by the engine at verdict time". Every
        // other route into GateRunner (conductor gate, the lane merge battery, the doctor's lints)
        // takes the default and cannot run a holdout at all.
        var gates = await GateRunner.RunAllAsync(_plan, log, ct, fastOnly,
            state.CurrentStage, stage?.Kind, onGates,
            store, state.RunId, headSha, includeHoldout: true).ConfigureAwait(false);
        await GateRunner.RunHookAsync(_plan, _plan.Teardown, "teardown", log, ct).ConfigureAwait(false);
        foreach (var g in gates)
        {
            var outcome = g.Cached ? "cached" : g.Skipped ? "skip" : g.Passed ? "pass" : g.Optional ? "warn" : "fail";
            logWithOutcome(OutcomeLine(g), outcome);
        }
        return gates;
    }

    /// <summary>SC4.1: the per-gate outcome line. A FAILURE carries the one comparison a human needs
    /// and this log has never printed — how long the gate took against how long it took the last time
    /// it passed. devcontext #12's wrong verdict was argued from duration by someone who had to time
    /// it by hand from the surrounding timestamps.</summary>
    private string OutcomeLine(GateResult g)
    {
        if (g.Cached) return $"gate {g.Name}: CACHED (0s)";
        if (g.Skipped) return $"gate {g.Name}: SKIP";
        var secs = $"{g.Duration.TotalSeconds:0}s";
        if (g.Passed)
            return g.Retried
                ? $"gate {g.Name}: PASS on retry ({secs}; the first attempt failed after {g.FirstAttemptDuration.TotalSeconds:0}s)"
                : $"gate {g.Name}: PASS ({secs})";
        return $"gate {g.Name}: {(g.Optional ? "WARN" : "FAIL")}{(g.Retried ? " after retry" : "")} ({secs} {VersusLastPass(g)})";
    }

    private string VersusLastPass(GateResult g)
    {
        var tier = _plan.Gates.FirstOrDefault(gc => string.Equals(gc.Name, g.Name, StringComparison.Ordinal))?.Tier ?? "full";
        if (store?.GetLastPassingGateDurationMs(state.RunId, g.Name, tier) is not { } ms || ms <= 0)
            return "— no passing run of this gate on record";
        var lastSeconds = ms / 1000.0;
        var delta = (g.Duration.TotalSeconds - lastSeconds) / lastSeconds * 100;
        return $"vs {lastSeconds:0}s when it last passed, {(delta >= 0 ? "+" : "")}{delta:0}%";
    }

    /// <summary>Persist gate results to the event log and run.db.</summary>
    public void PersistGates(IReadOnlyList<GateResult> gates, string scope, string? sessionId = null)
    {
        var head = Git.Head(_plan.Repo);
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
            var cfg = _plan.Gates.FirstOrDefault(gc => gc.Name == g.Name);
            var tier = cfg?.Tier ?? "full";
            // SC4.3: file the result under the SAME key the lookup will use — the gate's own working
            // directory and command text, not just this repo's HEAD. A pass filed under a bare HEAD
            // is a pass the next lookup can be served for a different command entirely.
            var sha = cfg is null ? head : GateRunner.CacheKey(_plan, cfg, head);
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

    /// <summary>Schedule the audit or confirming battery for a stage whose checkpoints are all DONE.
    /// KS3.4 round 8: the branch itself is <see cref="GateScheduling"/>'s — a pure function of the
    /// plan and the saved state — so <see cref="StageSelection.NextAction"/> can carry the decision
    /// THROUGH this scheduling to the session the same run composes after it, instead of stopping
    /// here and letting the loop re-decide behind every surface's back.</summary>
    public ScheduledWork ScheduleGateOrAudit(string stageId, string startHead, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var work = GateScheduling.Project(_plan, state, stageId, startHead);
        log(GateScheduling.Narrate(work, stageId));
        return work;
    }
}
