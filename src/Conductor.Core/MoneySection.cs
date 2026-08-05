using System.Globalization;
using System.Text;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Money;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// K4.3 — the money section of <c>REPORT.md</c>: the same rows <c>conductor money</c> prints, from the
/// same analyzer, so the report and the verb cannot disagree about what a checkpoint cost.
/// <para>Its own file rather than more of <see cref="Reporter"/> because it is its own job — reading a
/// database and pricing a run — and <c>Reporter.cs</c> is already at the line ceiling the architecture
/// ratchet enforces.</para>
/// </summary>
public static class MoneySection
{
    /// <summary>
    /// The run's money, read the way <c>conductor money</c> reads it: through <see cref="RunArchive"/>,
    /// which opens SQLite <c>Mode=ReadOnly</c>, so the report of a LIVE run cannot disturb the run
    /// writing it. Null when there is no database, no run id, or no spend recorded yet — the section
    /// then simply does not appear, which is the truth rather than a table of zeros.
    /// </summary>
    public static MoneyRun? Read(PlanConfig plan, string runId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            if (string.IsNullOrEmpty(runId)) return null;
            var archive = RunArchive.TryOpen(plan.RunDbPath);
            if (archive is null) return null;
            var costs = archive.Costs(runId);
            if (costs.Count == 0) return null;
            var sessions = archive.Sessions(runId);
            var windows = BudgetAnalyzer.Analyze(runId, plan.Name, sessions, archive.SoftBreaks(runId)).Windows;
            return MoneyAnalyzer.AnalyzeRun(runId, plan.Name, plan.Repo, null, null, sessions, costs, windows);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            // Same tolerance as every other read the report does: a report that loses one section
            // beats a run loop that throws while writing one.
            return null;
        }
    }

    /// <summary>Appends the section, or nothing at all when no money was spent.</summary>
    public static void Append(StringBuilder sb, MoneyRun? money)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (money is not { } m || m.Total.Tokens <= 0) return;

        sb.AppendLine("## Money");
        sb.AppendLine();
        sb.AppendLine("_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._");
        sb.AppendLine();
        sb.AppendLine("| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        sb.AppendLine(Row("**run total**", m.Total));
        if (m.Windows.Count > 1)
            foreach (var w in m.Windows) sb.AppendLine(Row("window " + w.Label, w));
        foreach (var s in m.Stages) sb.AppendLine(Row("stage " + s.Label, s));
        foreach (var month in m.Months) sb.AppendLine(Row(month.Label, month));
        sb.AppendLine();
        if (m.CapCostPayoff is { } payoff)
            sb.AppendLine($"_The last ceiling change bought **{payoff.ToString("0.0", CultureInfo.InvariantCulture)}×** " +
                          (payoff >= 1 ? "better" : "**worse**") + " dollars per delivered checkpoint._");
        if (m.Categories.Count > 0)
            sb.AppendLine("_Where the money goes: " + string.Join(" · ", m.Categories.Select(c =>
                $"{c.Label} ${c.Cost.ToString("0.00", CultureInfo.InvariantCulture)}" +
                (m.Total.Cost > 0 ? $" ({((double)(c.Cost / m.Total.Cost) * 100).ToString("0", CultureInfo.InvariantCulture)}%)" : ""))) +
                $" · blended ${(m.Total.CostPerMillionTokens ?? 0).ToString("0.00", CultureInfo.InvariantCulture)}/M tokens._");
        sb.AppendLine();
    }

    /// <summary>One row, formatted the way the verb formats it.</summary>
    private static string Row(string label, MoneyLine l) =>
        $"| {label} | {l.Sessions} | {BudgetAnalyzer.Millions(l.Tokens)} | " +
        $"{(l.CacheReadShare > 0 ? (l.CacheReadShare * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-")} | " +
        $"${l.Cost.ToString("0.00", CultureInfo.InvariantCulture)} | " +
        $"{(l.Checkpoints > 0 ? l.Checkpoints.ToString(CultureInfo.InvariantCulture) : "-")} | " +
        $"{(l.TokensPerCheckpoint is { } t ? BudgetAnalyzer.Millions((long)t) : "-")} | " +
        $"{(l.CostPerCheckpoint is { } c ? "$" + c.ToString("0.00", CultureInfo.InvariantCulture) : "-")} |";
}
