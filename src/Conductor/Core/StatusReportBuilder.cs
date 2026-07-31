using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// M5.6: folds <c>run.db</c>'s event log into a <see cref="StatusReport"/> in-process — no LLM, no
/// network — so <c>conductor status</c> answers from the database in well under a second. It reuses the
/// exact projection the control plane's <c>/state</c> endpoint uses
/// (<see cref="RunStateProjection.Fold"/> + <see cref="SnapshotBuilder.Build"/>), so the CLI verdict and
/// the Face never disagree.
/// </summary>
/// <remarks>
/// The live <see cref="RunState.Status"/> is a transient control field that is not on the event spine —
/// <see cref="RunStateProjection.Fold"/> never sets it — so the headline verdict is derived directly
/// from the event stream (last major event + interrupted-session detection) rather than from a folded
/// status that would always read <c>Idle</c>.
/// </remarks>
public static class StatusReportBuilder
{
    // SessionOutcomes that are not, on their own, a sign of trouble worth surfacing under "what hurt".
    // SC5.1: BlockedUntil belongs here — a session that correctly reported an external window and let
    // the engine sleep on it did its job; calling that "what hurt" would punish the right behaviour.
    private static readonly HashSet<string> HealthyOutcomes =
        new(StringComparer.OrdinalIgnoreCase) { "Advanced", "Progress", "RolledOver", "BlockedUntil" };

    /// <param name="isProcessAlive">Seam for tests: given a tracked pid and the instant it was tracked,
    /// is that same process still running? Production answers with <see cref="PidLiveness.LooksAlive"/>,
    /// so a recycled id cannot pass itself off as live work.</param>
    public static StatusReport Build(PlanConfig plan, IRunStore store,
        Func<int, DateTime, bool>? isProcessAlive = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        isProcessAlive ??= PidLiveness.LooksAlive;

        var runId = store.GetLatestRunId(plan.Name);
        if (string.IsNullOrEmpty(runId))
            return new StatusReport(plan.Name, "", "no run recorded yet — run `conductor run` at least once",
                "norun", 0, 0, 0, 0m, 0m, null, null, [], []);

        var events = store.ReadAllEvents(runId);
        var state = RunStateProjection.Fold(events);
        var track = ReadTrackerSafe(plan, ct);
        var snap = SnapshotBuilder.Build(plan, state, track);

        var (verdict, kind) = DeriveVerdict(events, store, runId, isProcessAlive, plan.StateDir);
        var whatHurt = DeriveWhatHurt(events);

        var stages = snap.Stages
            .Select(s => new StatusStageLine(s.Id, s.Title, s.Done, s.Total, s.State))
            .ToList();

        var sessions = state.History
            .OrderBy(h => h.Number)
            .TakeLast(8)
            .Select(h => new StatusSessionLine(
                h.Number, h.Stage, h.Kind.ToString(),
                h.Outcome?.ToString() ?? "running", h.CostUsd ?? 0m))
            .ToList();

        return new StatusReport(
            plan.Name, runId, verdict, kind,
            snap.DoneCount, snap.TotalCount, state.History.Count,
            snap.TotalCostUsd, snap.OverheadCostUsd,
            whatHurt, state.CurrentStage, stages, sessions);
    }

    private static (string verdict, string kind) DeriveVerdict(
        IReadOnlyList<ConductorEvent> events, IRunStore store, string runId,
        Func<int, DateTime, bool> isAlive, string stateDir)
    {
        var last = events.LastOrDefault(e => e is not TokenDelta);

        // Deliberate terminal / park states win over crash-detection: the engine was alive and chose to
        // finish or wait, so an unmatched session underneath is not a crash.
        switch (last)
        {
            case RunFinished f:
                return ($"{f.Status} — {f.CheckpointsDone}/{f.CheckpointsTotal} checkpoints over {f.Sessions} sessions", "ok");
            case AttentionRequested a:
                return ($"needs human — {a.Reason}", "attention");
            // SC5.1: a declared wait is the most deliberate park there is — the engine is alive and
            // asleep on purpose. Once the window has opened the run is merely idle again, and saying
            // "waiting" past the instant it was waiting for would be the same stale-sentence lie
            // SC2.2 removed from "what hurt".
            case RunBlockedUntil b:
                return DateTimeOffset.UtcNow < b.UntilUtc
                    ? (BlockedUntilRequest.Describe(b.UntilUtc, b.Reason), "waiting")
                    : ($"idle — the blocked-until window opened at {b.UntilUtc:yyyy-MM-dd HH:mm:ss}Z ({b.Reason}); resume with `conductor run`", "idle");
        }

        // A crash leaves a SessionStarted with no matching SessionFinished and no deliberate park after it.
        // If a process for this run is still alive it is genuinely running; if not, it was interrupted
        // (kill -9 / power loss / Ctrl-C).
        var interrupted = RunStateProjection.FindInterruptedSession(events);
        if (interrupted != null)
        {
            if (RunHasLiveProcess(store, runId, isAlive))
                return ($"running — session #{interrupted.Number} in {interrupted.StageId}", "active");

            // SC2.1: no live child does NOT mean no engine. SessionFinished is emitted only after the
            // verdict, so for the whole gate battery the engine is working with nothing spawned under it.
            // Its lock says so, and its own work is liveness — calling this a crash advised `conductor
            // run` against a healthy run.
            if (EngineLock.IsHeldByLiveEngine(stateDir))
                return ($"running — session #{interrupted.Number} in {interrupted.StageId}: agent exited, engine still working (verdict and gates)", "active");

            return ($"interrupted mid-session — #{interrupted.Number} in {interrupted.StageId} never finished; resume with `conductor run`",
                "interrupted");
        }

        return last switch
        {
            SessionFinished sf => ($"idle — last was session #{sf.Number} in {sf.StageId}: {sf.Outcome}", "idle"),
            StageConfirmed sc => ($"idle — stage {sc.StageId} confirmed", "idle"),
            null => ("idle — no activity recorded yet", "idle"),
            _ => ($"idle — last event was {last.GetType().Name}", "idle"),
        };
    }

    private static bool RunHasLiveProcess(IRunStore store, string runId, Func<int, DateTime, bool> isAlive)
    {
        foreach (var p in store.GetAllPids(runId))
            if (p.ExitedUtc == null && isAlive(p.Pid, p.StartedUtc))
                return true;
        return false;
    }

    /// <summary>
    /// SC2.2. "what hurt" used to be a sentence with no age that never went away: the newest failure in
    /// the whole log won, so a gate that failed once and passed on every run since, or a park the operator
    /// cleared hours ago, still read as the current complaint. Two rules fix that, and both are
    /// measurements rather than heuristics:
    /// <list type="bullet">
    ///   <item><description>a failure is <em>cleared</em> once the thing that failed has since succeeded —
    ///   a later passing run of that same gate, or a <see cref="StageConfirmed"/>, which only happens on a
    ///   green battery and therefore clears everything older than it;</description></item>
    ///   <item><description>whatever survives carries its age and wall-clock, so a reader can tell a
    ///   four-second-old failure from a four-hour-old one without opening the log.</description></item>
    /// </list>
    /// </summary>
    private static string? DeriveWhatHurt(IReadOnlyList<ConductorEvent> events, DateTimeOffset? now = null)
    {
        // Gates that have passed since (walking backwards, so "since" = already seen).
        var passedSince = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = events.Count - 1; i >= 0; i--)
        {
            switch (events[i])
            {
                // A confirmed stage is a green full battery: nothing older than it is still hurting.
                case StageConfirmed:
                case OwnerApprovalGranted:
                    return null;
                case AttentionRequested a:
                    return a.Reason + Staleness.Since(a.Ts, now);
                case GateFinished { Passed: true } ok:
                    passedSince.Add(ok.Name);
                    break;
                case GateFinished g when !g.Passed && !g.Skipped && !g.Optional && !passedSince.Contains(g.Name):
                    return $"gate '{g.Name}' failed{(g.Scope is { Length: > 0 } sc ? $" ({sc})" : "")}"
                           + Staleness.Since(g.Ts, now);
            }
        }
        // No hard failure — surface a non-advancing last session, if any.
        var lastFinished = events.OfType<SessionFinished>().LastOrDefault();
        if (lastFinished != null && !HealthyOutcomes.Contains(lastFinished.Outcome))
            return $"session #{lastFinished.Number} ended {lastFinished.Outcome}" + Staleness.Since(lastFinished.Ts, now);
        return null;
    }

    private static TrackerSnapshot ReadTrackerSafe(PlanConfig plan, CancellationToken ct)
    {
        try { return ProgressProviderFactory.Create(plan).Read(plan, ct); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

}
