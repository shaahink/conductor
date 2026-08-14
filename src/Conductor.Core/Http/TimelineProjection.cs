using Conductor.Core.Events;

namespace Conductor.Core.Http;

/// <summary>
/// M5.1's <c>GET /timeline</c> fold, lifted out of the live server so KS2.2's archive plane serves the
/// same shape from the same rules. It was already a pure function of the event log — a switch with no
/// reference to <c>_plan</c>, <c>_state</c> or the store — and copying it into the archive would have
/// left two timelines that drift the first time an event type is added to one of them.
/// </summary>
public static class TimelineProjection
{
    /// <summary>Every event worth a row, in log order. Events with no timeline meaning (token deltas,
    /// anything this switch does not name) are skipped rather than rendered as "unknown".</summary>
    public static TimelineDto From(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var entries = new List<TimelineEntryDto>();
        foreach (var evt in events)
        {
            string kind, desc;
            string? stageId = null, outcome = null;
            int? sessionNum = null;
            decimal? cost = null;

            switch (evt)
            {
                case SessionStarted s:
                    kind = "session";
                    desc = $"session #{s.Number} {s.Kind} started";
                    stageId = s.StageId;
                    sessionNum = s.Number;
                    break;
                case SessionFinished f:
                    kind = "session";
                    desc = $"session #{f.Number} finished: {f.Outcome}";
                    stageId = f.StageId;
                    sessionNum = f.Number;
                    cost = f.CostUsd;
                    outcome = f.Outcome;
                    break;
                case GateFinished g:
                    kind = "gate";
                    desc = $"gate {g.Name}: {(g.Passed ? "pass" : "FAIL")} ({g.DurationMs}ms)";
                    stageId = g.Scope;
                    outcome = g.Passed ? "pass" : "fail";
                    break;
                case TokenDelta:
                    continue; // skip — too noisy for timeline
                case AttentionRequested a:
                    kind = "attention";
                    desc = $"needs human: {a.Reason}";
                    break;
                case StageEntered se:
                    kind = "stage";
                    desc = $"stage {se.StageId} entered";
                    stageId = se.StageId;
                    break;
                case StageConfirmed sConfirmed:
                    kind = "stage";
                    desc = $"stage {sConfirmed.StageId} confirmed";
                    stageId = sConfirmed.StageId;
                    break;
                case PlanReloaded p:
                    kind = "run";
                    desc = $"plan reloaded — v{p.PlanVersion} · {p.Stages} stages · {p.Gates} gates";
                    break;
                default:
                    continue;
            }
            entries.Add(new TimelineEntryDto(
                Utc: evt.Ts.ToString("O"),
                Kind: kind,
                Description: desc,
                StageId: stageId,
                SessionNumber: sessionNum,
                CostUsd: cost,
                Outcome: outcome));
        }
        return new TimelineDto(entries);
    }
}
