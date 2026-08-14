using Conductor.Core.Events;

namespace Conductor.Core.Http;

/// <summary>
/// M5.1's <c>GET /timeline</c> fold, lifted out of the live server so KS2.2's archive plane serves the
/// same shape from the same rules. It was already a pure function of the event log — a switch with no
/// reference to <c>_plan</c>, <c>_state</c> or the store — and copying it into the archive would have
/// left two timelines that drift the first time an event type is added to one of them.
///
/// <para><b>KS2.2 changed the live payload on purpose, and this is the record of it.</b> The switch this
/// was lifted from opened with <c>string kind = "unknown", desc = "";</c> and answered a
/// <see cref="TokenDelta"/> with a bare <c>break</c>. A <c>break</c> leaves the switch and falls into the
/// <c>entries.Add</c> below it, so every token delta — one per deduplicated API call — became a row with
/// kind <c>unknown</c> and no description at all. Measured against a live engine before the change:
/// 2262 timeline entries, 2147 of them blank <c>unknown</c> rows. The Face renders the spine without any
/// kind or description filter (<c>tab_history.go</c>), so those rows were 95% of what a reader scrolled
/// through and none of them said anything.</para>
///
/// <para>They are dropped now, on BOTH planes, and that is a deliberate change to what
/// <c>GET /timeline</c> answers rather than a refactor that preserved behaviour. The comment beside the
/// live endpoint says the same, and <c>KS2_2TimelineProjectionTests</c> pins it on both planes so no
/// future event type can quietly re-open the hole: every row this fold emits carries a named kind and a
/// description with words in it, or it is not emitted.</para>
/// </summary>
public static class TimelineProjection
{
    /// <summary>Every event worth a row, in log order. An event with no timeline meaning — a
    /// <see cref="TokenDelta"/>, or anything this switch does not name — produces NO row. There is no
    /// "unknown" kind and no blank description on the wire; see the type remarks for what that changed.</summary>
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
                // `continue`, not the `break` this switch used to carry: a break fell through to the
                // entries.Add below and shipped a blank `unknown` row per API call. Named explicitly
                // rather than left to `default` so the intent survives the next edit.
                case TokenDelta:
                    continue; // no timeline meaning — LiveMetrics owns token/cost accrual
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
