using System.Globalization;
using System.Text;

using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// SC2.4 — the artifact that outlives the engine. When a run completes, the control plane dies with
/// the process and every live surface (Face, <c>/state</c>, the dashboard) goes with it; REPORT.md is
/// a snapshot of a run in flight, not a closing statement, and it is rewritten by the next run in the
/// same state dir. So a finished run left nothing that answered "what did that cost, how many
/// sessions, which stages fought back" once the process exited (sk-platform #6).
///
/// <para>Everything here is read back out of <c>run.db</c> — the sessions table and its correlated
/// cost rows — not out of the in-memory <see cref="RunState"/>, so the same summary can be rebuilt
/// long after the engine is gone. <see cref="RunState"/> is used only for the figures the database
/// does not hold: the budget window and the skipped-stage set.</para>
/// </summary>
public static class RunSummary
{
    public static string SummaryPath(PlanConfig plan) => Path.Combine(plan.StateDir, "RUN-SUMMARY.md");

    /// <summary>One stage's line in the summary: how many sessions it took, how many attempts the
    /// hardest of them needed, and what it cost.</summary>
    public sealed record StageLine(string Id, string Title, int Sessions, int Attempts, decimal CostUsd, string State);

    /// <summary>A session whose outcome was NOT <see cref="SessionOutcome.Advanced"/> — the sessions
    /// that cost money without closing a checkpoint. The spec asks for these by name because they are
    /// the only per-session detail worth keeping after the run.</summary>
    public sealed record RoughSession(int Number, string Stage, string Kind, string Outcome, int Attempt, decimal CostUsd);

    public static string Build(
        PlanConfig plan, RunState state, TrackerSnapshot track, IRunStore? store, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);

        var sessions = ReadSessions(store, state);
        var run = ReadRun(store, state);
        var startedUtc = ParseUtc(run?.StartedUtc)
            ?? sessions.Select(s => ParseUtc(s.StartedUtc)).Where(t => t != null).Min()
            ?? nowUtc;
        var endedUtc = ParseUtc(run?.EndedUtc) ?? nowUtc;
        var (agentCost, overhead) = ReadCostSplit(store, state);
        var done = track.Checkpoints.Count(c => c.IsDone);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Run summary — {plan.Name}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"_Written {nowUtc:yyyy-MM-dd HH:mm} UTC when the run reached {state.Status}. Rebuilt from `run.db`, so it survives the engine._");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Plan:** {plan.Name} · run `{state.RunId}`");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Repo:** {plan.Repo} · branch `{run?.Branch ?? Git.Branch(plan.Repo)}` · HEAD `{Short(Git.Head(plan.Repo))}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Outcome:** {state.Status}{SkippedSuffix(state)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Wall clock:** {startedUtc:yyyy-MM-dd HH:mm} UTC → {endedUtc:yyyy-MM-dd HH:mm} UTC · {Duration(endedUtc - startedUtc)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Sessions:** {sessions.Count}{KindBreakdown(sessions)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Checkpoints:** {done}/{track.Checkpoints.Count} done");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Spend:** {SpendLine(plan, state, agentCost, overhead)}");
        sb.AppendLine();

        sb.AppendLine("## Stages");
        sb.AppendLine();
        sb.AppendLine("| Stage | Title | Sessions | Attempts | Cost | State |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var line in StageLines(plan, state, track, sessions))
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {line.Id} | {line.Title} | {line.Sessions} | {line.Attempts} | ${line.CostUsd:0.0000} | {line.State} |");
        sb.AppendLine();

        var rough = RoughSessions(sessions);
        sb.AppendLine("## Sessions that did not advance");
        sb.AppendLine();
        if (rough.Count == 0)
        {
            sb.AppendLine("None — every recorded session ended Advanced.");
        }
        else
        {
            sb.AppendLine("| # | Stage | Kind | Outcome | Attempt | Cost |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var r in rough)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {r.Number} | {r.Stage} | {r.Kind} | {r.Outcome} | {r.Attempt} | ${r.CostUsd:0.0000} |");
        }
        sb.AppendLine();

        if (state.SkippedStages.Count > 0)
        {
            sb.AppendLine("## Skipped stages (never delivered)");
            sb.AppendLine();
            foreach (var s in state.SkippedStages.OrderBy(x => x, StringComparer.Ordinal))
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- {s} — {plan.Stages.FirstOrDefault(p => string.Equals(p.Id, s, StringComparison.Ordinal))?.Title ?? "(not in plan)"}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Writes the summary next to REPORT.md. Never throws: a run that finished must not fail
    /// its own completion because a disk write lost a race.</summary>
    public static void Write(
        PlanConfig plan, RunState state, TrackerSnapshot track, IRunStore? store, Action<string> log,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            Directory.CreateDirectory(plan.StateDir);
            File.WriteAllText(SummaryPath(plan), Build(plan, state, track, store, nowUtc ?? DateTime.UtcNow),
                Reporter.Utf8Bom);
            log($"run summary written to {SummaryPath(plan)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"run summary write failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- pieces

    private static RunRow? ReadRun(IRunStore? store, RunState state)
    {
        if (store == null || string.IsNullOrEmpty(state.RunId)) return null;
        try { return store.QueryRun(state.RunId); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return null; }
    }

    /// <summary>Agent spend vs overhead, split by the <c>costs</c> category the engine actually wrote.
    /// The per-session cost column sums every category, so deriving overhead as "total minus agent"
    /// from that column would count gate time twice; the split has to come from the category rows.</summary>
    internal static (decimal Agent, decimal Overhead) ReadCostSplit(IRunStore? store, RunState state)
    {
        if (store != null && !string.IsNullOrEmpty(state.RunId))
        {
            try
            {
                var totals = store.QueryCostTotals(state.RunId);
                if (totals.Count > 0)
                    return (totals.Where(t => string.Equals(t.Category, "agent", StringComparison.Ordinal)).Sum(t => t.CostUsd),
                            totals.Where(t => !string.Equals(t.Category, "agent", StringComparison.Ordinal)).Sum(t => t.CostUsd));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // fall through to the in-memory history
            }
        }
        return (state.TotalCostUsd, state.TotalOverheadCostUsd);
    }

    internal static IReadOnlyList<SessionSummaryRow> ReadSessions(IRunStore? store, RunState state)
    {
        if (store != null && !string.IsNullOrEmpty(state.RunId))
        {
            try
            {
                var rows = store.QuerySessions(state.RunId);
                if (rows.Count > 0) return rows.OrderBy(r => r.Number).ToList();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // fall through to the in-memory history — a summary from RAM beats no summary
            }
        }
        // No store (or an empty one): the in-memory history is the same shape, one row per session.
        return state.History.Select(h => new SessionSummaryRow(
            Number: h.Number, StageId: h.Stage, Kind: h.Kind.ToString(),
            StartedUtc: h.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            EndedUtc: h.EndedUtc?.ToString("O", CultureInfo.InvariantCulture),
            Outcome: h.Outcome?.ToString(), Attempt: h.Attempt, ResumeCount: h.ResumeCount,
            GateSummary: h.GateSummary, ResultSummary: h.ResultSummary,
            CommitCount: h.NewCommits?.Count ?? 0, CostUsd: (double)(h.CostUsd ?? 0m),
            TokensIn: h.TokensInput ?? 0, TokensOut: h.TokensOutput ?? 0,
            TokensThink: h.TokensReasoning ?? 0, TokensCache: 0)).ToList();
    }

    internal static IReadOnlyList<StageLine> StageLines(
        PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<SessionSummaryRow> sessions)
    {
        var lines = new List<StageLine>();
        foreach (var s in plan.Stages)
        {
            var mine = sessions.Where(r => string.Equals(r.StageId, s.Id, StringComparison.Ordinal)).ToList();
            var rows = track.ForStage(s.Id).ToList();
            var d = rows.Count(r => r.IsDone);
            var st = state.SkippedStages.Contains(s.Id) ? "skipped"
                : state.ConfirmedStages.Contains(s.Id) ? "confirmed"
                : rows.Count > 0 && d == rows.Count ? "done"
                : mine.Count > 0 ? $"incomplete ({d}/{rows.Count})"
                : "never entered";
            lines.Add(new StageLine(
                Id: s.Id, Title: s.Title, Sessions: mine.Count,
                // The attempt counter resets per stage, so the highest attempt a stage ever reported IS
                // the number of tries it took — a stage that needed three goes reads 3, not 1.
                Attempts: mine.Count == 0 ? 0 : mine.Max(r => r.Attempt),
                CostUsd: mine.Sum(r => (decimal)r.CostUsd),
                State: st));
        }
        return lines;
    }

    internal static IReadOnlyList<RoughSession> RoughSessions(IReadOnlyList<SessionSummaryRow> sessions)
        => sessions
            .Where(s => !string.Equals(s.Outcome, nameof(SessionOutcome.Advanced), StringComparison.Ordinal))
            .Select(s => new RoughSession(s.Number, s.StageId, s.Kind, s.Outcome ?? "unrecorded", s.Attempt,
                (decimal)s.CostUsd))
            .ToList();

    internal static string SpendLine(PlanConfig plan, RunState state, decimal agentCost, decimal overhead)
    {
        var total = agentCost + overhead;
        var cap = plan.Limits.MaxRunCostUsd;
        var vsCap = cap is > 0
            ? $" · cap ${cap:0.00} ({(cap.Value <= 0 ? 0 : total / cap.Value * 100m):0.#}% used)"
            : " · no cap set (limits.maxRunCostUsd unset)";
        // SC2.3 taught this one: after an owner approves past a budget park, PerRunCostUsd is the
        // WINDOW since that approval, not the run. Saying which is which is the whole point.
        var window = state.BudgetApprovals > 0
            ? $" · window since approval #{state.BudgetApprovals} (${state.PerRunCostUsd:0.0000})"
            : "";
        return $"${total:0.0000} total (agent ${agentCost:0.0000} + gates ${overhead:0.0000}){vsCap}{window}";
    }

    private static string KindBreakdown(IReadOnlyList<SessionSummaryRow> sessions)
    {
        if (sessions.Count == 0) return "";
        var byKind = sessions.GroupBy(s => s.Kind, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        return $" ({string.Join(", ", byKind)})";
    }

    private static string SkippedSuffix(RunState state)
        => state.SkippedStages.Count > 0
            ? $" — EXCEPT skipped stages: {string.Join(", ", state.SkippedStages.OrderBy(x => x, StringComparer.Ordinal))}"
            : "";

    internal static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{d.TotalSeconds:0.#}s";
    }

    private static DateTime? ParseUtc(string? iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
