using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// Closing the plan: the last gate battery, and what a finished run leaves behind. Split out of
/// <c>VerdictEngine.cs</c> when SC2.4 gave completion a second job — writing RUN-SUMMARY.md — and the
/// file went past its 500-line ceiling. These two methods are the run loop's only exit into "done".
/// </summary>
public sealed partial class VerdictEngine
{
    internal async Task<bool> ConfirmCompletionAsync(CancellationToken ct)
    {
        var lastOutcome = _ctx.State.History.LastOrDefault()?.Outcome;
        if (_ctx.LastGates != null && GateRunner.AllRequiredPassed(_ctx.LastGates) &&
            lastOutcome is SessionOutcome.Advanced or SessionOutcome.Progress)
            return true;

        _ctx.Log("tracker reports all checkpoints DONE — running the gate battery to confirm before closing the plan");
        _ctx.State.Status = RunStatus.VerifyingGates;
        _ctx.Save();
        _pushIdleSnapshot();
        var gates = await RunGateBatteryAsync(ct).ConfigureAwait(false);
        _ctx.LastGates = gates;
        _ctx.State.Status = RunStatus.Idle;
        EmitGates(gates, "completion");
        _ctx.RunOverheadUsd += gates.Sum(g => g.EstimatedCostUsd(_ctx.Plan.Limits.OverheadCostPerSecond));
        _ctx.State.PerRunOverheadCostUsd = _ctx.RunOverheadUsd;
        if (GateRunner.AllRequiredPassed(gates)) return true;

        _ctx.State.AttemptsThisStage++;
        _ctx.State.PendingFix = new PendingFix
        {
            FromSession = _ctx.State.History.LastOrDefault()?.Number ?? 0,
            GateFailures = GateRunner.FailureDetails(gates),
            ProgressSummary = "tracker claims all checkpoints DONE, but the gate battery is red — the claims are not yet true",
        };
        _ctx.Log("completion NOT confirmed — gates red; queuing a fix session");
        _ctx.Save();
        return false;
    }

    internal void CompletePlan(TrackerSnapshot track)
    {
        _ctx.State.Status = RunStatus.Completed;
        _ctx.State.SetAttention(_ctx.State.SkippedStages.Count > 0
            ? $"plan complete EXCEPT skipped stages: {string.Join(", ", _ctx.State.SkippedStages)}"
            : null);
        _ctx.Log($"🎉 plan '{_ctx.Plan.Name}' complete — {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} checkpoints done");
        _ctx.Events.Emit(new RunFinished
        {
            Status = _ctx.State.Status.ToString(),
            Sessions = _ctx.State.SessionCounter,
            CheckpointsDone = track.Checkpoints.Count(c => c.IsDone),
            CheckpointsTotal = track.Checkpoints.Count,
        });
        _ctx.Store?.RecordRunEnd(_ctx.State.RunId, _ctx.State.Status.ToString());
        _saveAndReport();
        // SC2.4: the control plane dies with this process and REPORT.md is a mid-flight snapshot the
        // next run in this state dir overwrites. RUN-SUMMARY.md is the closing statement — written
        // AFTER RecordRunEnd so it can read the run's own ended_utc back out of run.db.
        RunSummary.Write(_ctx.Plan, _ctx.State, track, _ctx.Store, _ctx.Log);
        // FU-OWNER-11: repo and engine build ride the run-end message for the same reason they ride
        // the run-start one — this is the message an owner reads hours later, and it is the last
        // chance to say which checkout finished and which binary finished it.
        Notify($"Conductor: plan {_ctx.Plan.Name} COMPLETE ({_ctx.State.SessionCounter} sessions) — " +
               $"repo {_ctx.Plan.Repo} · engine {BuildInfo.Current.Full}");
    }
}
