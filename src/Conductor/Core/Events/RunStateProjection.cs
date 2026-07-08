using Conductor.Models;

namespace Conductor.Core.Events;

/// <summary>
/// B2.2 — the first projection over the append-only event log. Folds a <see cref="ConductorEvent"/>
/// stream back into the durable spine of a <see cref="RunState"/>: plan identity, current stage,
/// the session counter, confirmed/audited stages, and the session <see cref="RunState.History"/>
/// with its cost/token metrics. Proven equal to the legacy <c>state.json</c> for a recorded run by
/// <see cref="StateProjectionParity"/>, which is the precondition (D-5) for ever treating the log as
/// the source of truth.
/// </summary>
/// <remarks>
/// Delivered <em>additively</em>: <c>state.json</c> is still written and remains authoritative for
/// the transient control fields the log does not yet carry (see <see cref="StateProjectionParity"/>
/// for the exact parity surface). The fold is pure — it depends only on the events, never on disk or
/// wall-clock — so it is deterministic and unit-testable. Later projections (Timeline, Metrics,
/// Health — B5) consume the same stream; this one owns the <see cref="RunState"/> reconstruction.
/// </remarks>
public static class RunStateProjection
{
    /// <summary>Rebuild the <see cref="RunState"/> spine by folding the log in sequence order.</summary>
    public static RunState Fold(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var state = new RunState();
        // Sessions are indexed by their 1-based number so a later SessionFinished can complete the
        // record opened by its SessionStarted, regardless of interleaving across restarts.
        var byNumber = new Dictionary<int, SessionRecord>();

        foreach (var evt in events.OrderBy(e => e.Seq))
        {
            if (!string.IsNullOrEmpty(evt.RunId)) state.RunId = evt.RunId;

            switch (evt)
            {
                case RunStarted e:
                    state.PlanName = e.Plan;
                    break;

                case StageEntered e:
                    // The orchestrator emits StageEntered only on a real stage change (Orchestrator
                    // resets attempts there); the projection tracks the current stage + audit baseline.
                    state.CurrentStage = e.StageId;
                    state.CurrentStageStartHead = e.StartHead;
                    break;

                case SessionStarted e:
                    state.SessionCounter = Math.Max(state.SessionCounter, e.Number);
                    var rec = new SessionRecord
                    {
                        Number = e.Number,
                        Stage = e.StageId,
                        Kind = ParseKind(e.Kind),
                        Attempt = e.Attempt,
                        StartedUtc = evt.Ts.UtcDateTime,
                        ClaudeSessionId = e.AgentSessionId ?? "",
                    };
                    state.History.Add(rec);
                    byNumber[e.Number] = rec;
                    break;

                case SessionFinished e when byNumber.TryGetValue(e.Number, out var fin):
                    fin.EndedUtc = evt.Ts.UtcDateTime;
                    fin.Outcome = ParseOutcome(e.Outcome);
                    fin.NewCommits = [.. e.NewCommits];
                    fin.NewlyDone = [.. e.NewlyDone];
                    fin.CostUsd = e.CostUsd;
                    fin.TokensInput = e.TokensInput;
                    fin.TokensOutput = e.TokensOutput;
                    fin.TokensReasoning = e.TokensReasoning;
                    fin.TokensCacheRead = e.TokensCacheRead;
                    break;

                case StageConfirmed e:
                    if (!state.ConfirmedStages.Contains(e.StageId)) state.ConfirmedStages.Add(e.StageId);
                    if (e.Audited && !state.AuditedStages.Contains(e.StageId)) state.AuditedStages.Add(e.StageId);
                    break;

                // GateFinished / CheckpointConfirmed / AttentionRequested / RunFinished belong to the
                // Timeline / Metrics / Health projections (B5), not the RunState spine — folded there.
            }
        }

        return state;
    }

    private static SessionKind ParseKind(string kind) =>
        Enum.TryParse<SessionKind>(kind, ignoreCase: true, out var k) ? k : SessionKind.Deliver;

    private static SessionOutcome? ParseOutcome(string outcome) =>
        Enum.TryParse<SessionOutcome>(outcome, ignoreCase: true, out var o) ? o : null;
}
