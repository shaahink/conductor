using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Models;

namespace Conductor.Core.Watch;

/// <summary>
/// SF5.1 — the ~30-line JSON brief a wake hands to whoever is supervising: a human reading it in a
/// terminal, a headless model invocation reading it on stdin, or a cloud session reading it out of a
/// webhook body.
///
/// <para>Sized on purpose. The whole point of an event-driven watch is that the expensive reader is
/// only ever handed the moment that needs judgment, so the brief must answer "what fired, where are
/// we, what is it costing, what can I do about it" without the reader having to go and look. Bigger
/// than that and the supervisor pays the accumulation cost the polling babysitter paid; smaller and
/// it has to run three verbs before it can think, which costs more than the lines saved.</para>
/// </summary>
public static class WatchBrief
{
    /// <summary>Stage rows carried. The board is orientation, not a report — the reader has
    /// <c>conductor status</c> for the whole thing.</summary>
    public const int MaxStageRows = 5;

    /// <summary>Recent sessions carried, newest first.</summary>
    public const int MaxSessionRows = 3;

    /// <summary>The reason string a supervisor branches on. <see cref="WatchReason.OwnerPark"/> is the
    /// one the event cannot resolve alone: an owner gate and a budget cap both emit
    /// <c>ownerApprovalRequested</c>, and only the run state knows which — so it is split here, where
    /// the state is in hand, and falls back to the honest <c>owner-park</c> when there is no state to
    /// read rather than guessing at a specific one.</summary>
    public static string ReasonSlug(WatchWake wake, RunState? state)
    {
        ArgumentNullException.ThrowIfNull(wake);
        return wake.Reason switch
        {
            WatchReason.NeedsHuman => "needs-human",
            WatchReason.OwnerPark => state?.AwaitingOwnerReason switch
            {
                AwaitingOwnerReason.Budget => "budget-park",
                AwaitingOwnerReason.OwnerGate => "owner-gate",
                AwaitingOwnerReason.ApprovalMode => "approval-park",
                _ => "owner-park",
            },
            WatchReason.CircuitBreaker => "circuit-breaker",
            WatchReason.PhaseRedTwice => "phase-red-twice",
            WatchReason.EngineGone => "engine-gone",
            WatchReason.RunEnded => "run-ended",
            WatchReason.Timeout => "timeout",
            _ => "unknown",
        };
    }

    /// <summary>What a supervisor can actually do about this wake, in the order to try them.</summary>
    public static IReadOnlyList<string> SuggestedVerbs(string reasonSlug) => reasonSlug switch
    {
        "needs-human" => ["conductor status", "conductor resume", "conductor skip"],
        "owner-gate" => ["conductor status", "conductor approve"],
        "approval-park" => ["conductor status", "conductor approve"],
        "budget-park" => ["conductor status", "conductor approve (resets the budget window)"],
        "circuit-breaker" => ["conductor status", "conductor inject \"<what to try instead>\"", "conductor pause"],
        "phase-red-twice" => ["conductor gate --full", "conductor inject \"<what the battery says>\"", "conductor pause"],
        "engine-gone" => ["conductor status", "conductor run (resumes from saved state)"],
        "run-ended" => ["conductor status", "conductor report"],
        "timeout" => ["conductor status"],
        _ => ["conductor status"],
    };

    /// <summary>Build the brief. Every input is optional except the wake and the plan, because a
    /// supervisor woken by an engine that vanished mid-write must still get a brief rather than an
    /// exception — a watch that throws where a run crashed is a second outage, not a report.</summary>
    public static JsonObject Build(
        WatchWake wake,
        PlanConfig plan,
        RunState? state,
        StatusReport? status,
        bool engineAlive,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(plan);

        var slug = ReasonSlug(wake, state);
        var o = new JsonObject
        {
            ["reason"] = slug,
            ["firedFrom"] = wake.FiredFrom,
            ["detail"] = wake.Detail,
            ["at"] = nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["plan"] = plan.Name,
            ["repo"] = plan.Repo,
            ["runId"] = status?.RunId ?? state?.RunId ?? "",
            ["status"] = (state?.Status ?? RunStatus.Idle).ToString(),
            ["engineAlive"] = engineAlive,
            ["stage"] = wake.StageId ?? state?.CurrentStage ?? status?.CurrentStageId,
            ["attempt"] = state?.AttemptsThisStage ?? 0,
            ["sessions"] = status?.SessionCount ?? state?.History.Count ?? 0,
            ["checkpoints"] = status is null ? null : $"{status.DoneCount}/{status.TotalCount}",
            ["spendUsd"] = decimal.Round(state?.PerRunCostUsd ?? status?.TotalCostUsd ?? 0m, 2),
            ["costCapUsd"] = plan.Limits?.MaxRunCostUsd is { } cap ? decimal.Round(cap, 2) : null,
            ["tokens"] = state?.PerRunTokens ?? 0,
            ["tokenCap"] = plan.Limits?.MaxRunTokens,
            ["attention"] = state?.AttentionReason,
            ["whatHurt"] = status?.WhatHurt,
        };

        o["stages"] = StageRows(status);
        o["recentSessions"] = SessionRows(status);
        o["suggest"] = new JsonArray([.. SuggestedVerbs(slug).Select(v => (JsonNode)JsonValue.Create(v)!)]);
        return o;
    }

    /// <summary>The brief as the text that actually leaves the process — indented, so a human reading
    /// it in a terminal and a model reading it on stdin see the same ~30 lines.</summary>
    public static string Render(JsonObject brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        return brief.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // The board is centred on where the run actually is: the current stage and its neighbours, not
    // the first five rows of a 24-checkpoint plan that were confirmed days ago.
    private static JsonArray StageRows(StatusReport? status)
    {
        var rows = new JsonArray();
        if (status is null || status.Stages.Count == 0) return rows;

        var stages = status.Stages;
        var centre = Math.Max(0, stages.ToList().FindIndex(s =>
            string.Equals(s.Id, status.CurrentStageId, StringComparison.OrdinalIgnoreCase)));
        var start = Math.Clamp(centre - 1, 0, Math.Max(0, stages.Count - MaxStageRows));

        foreach (var s in stages.Skip(start).Take(MaxStageRows))
            rows.Add((JsonNode)JsonValue.Create($"{s.Id} {s.Done}/{s.Total} {s.State}")!);
        return rows;
    }

    private static JsonArray SessionRows(StatusReport? status)
    {
        var rows = new JsonArray();
        if (status is null) return rows;
        foreach (var s in status.RecentSessions.Take(MaxSessionRows))
            rows.Add((JsonNode)JsonValue.Create(
                $"#{s.Number} {s.Stage} {s.Kind} {s.Outcome} ${s.CostUsd:0.00}")!);
        return rows;
    }
}
