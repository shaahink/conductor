using System.Globalization;
using System.Text;

using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>
/// SF0.4 — what a finished run owes its operator about the bugs it did not fix.
///
/// <para>A run that ends with open bugs used to say nothing about them. The rows were in
/// <c>run.db</c>, but the console printed <c>run ended</c> and stopped, and RUN-SUMMARY.md — the
/// artifact written precisely so a finished run leaves something behind — listed sessions, stages and
/// spend and omitted the ledger entirely. The next run in the repo then started a fresh run id and,
/// before <see cref="IRunStore.QueryCarriedBugs"/>, could not see those rows either. Three surfaces
/// each dropped the same fact, so eleven open bugs made it out of the Sarban core run in a hand-typed
/// markdown table and nowhere else.</para>
///
/// <para>Both renderings live here so the count on screen and the count in the file cannot drift, and
/// both read the database rather than in-memory state — the summary is rebuildable long after the
/// engine is gone.</para>
/// </summary>
public static class OpenBugsReport
{
    /// <summary>Open bugs this run filed, and open bugs carried in from earlier runs in the same
    /// <c>run.db</c>. Both are outstanding work in this repo; the split is only about provenance.</summary>
    public sealed record Counts(int ThisRun, int Carried)
    {
        public int Total => ThisRun + Carried;
        public static readonly Counts None = new(0, 0);
    }

    /// <summary>Never throws: counting the ledger must not be able to fail a run's own completion.</summary>
    public static Counts Count(IRunStore? store, string? runId)
    {
        if (store == null || string.IsNullOrEmpty(runId)) return Counts.None;
        try
        {
            return new Counts(
                store.QueryBugs(runId, status: "open").Count,
                store.QueryCarriedBugs(runId).Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                   or Microsoft.Data.Sqlite.SqliteException)
        {
            return Counts.None;
        }
    }

    /// <summary>The one line the run-end epilogue prints, or null when nothing is outstanding. Says how
    /// many and where — a count with no way to read the rows is a number the operator cannot act on.</summary>
    public static string? EpilogueLine(Counts counts, string planPathArg)
    {
        if (counts.Total == 0) return null;
        var carried = counts.Carried > 0
            ? $" ({counts.Carried} carried from an earlier run in this repo)"
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{counts.Total} open bug(s){carried} — conductor bug list -p {planPathArg}");
    }

    /// <summary>The RUN-SUMMARY.md section. Titles are included because the summary has to be readable
    /// on its own: it is the thing that survives when run.db is a directory nobody opens.</summary>
    public static string Markdown(IRunStore? store, string? runId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Open bugs at run end");
        sb.AppendLine();

        IReadOnlyList<BugRow> mine = [];
        IReadOnlyList<CarriedBugRow> carried = [];
        if (store != null && !string.IsNullOrEmpty(runId))
        {
            try
            {
                mine = store.QueryBugs(runId, status: "open");
                carried = store.QueryCarriedBugs(runId);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or Microsoft.Data.Sqlite.SqliteException)
            {
                sb.AppendLine("_The bug ledger could not be read._");
                sb.AppendLine();
                return sb.ToString();
            }
        }

        if (mine.Count == 0 && carried.Count == 0)
        {
            sb.AppendLine("None — every tracked bug filed in this repo is closed.");
            sb.AppendLine();
            return sb.ToString();
        }

        sb.AppendLine("These outlive this run. `conductor bug list` in this repo shows them to the next run,");
        sb.AppendLine("and `conductor bug fix <id>` closes them from there.");
        sb.AppendLine();
        sb.AppendLine("| # | Severity | Stage | Title | Filed by |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var b in mine)
            Row(sb, b, "this run");
        foreach (var c in carried)
            Row(sb, c.Bug, string.IsNullOrWhiteSpace(c.PlanName) ? "an earlier run" : c.PlanName);
        sb.AppendLine();
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, BugRow b, string filedBy) =>
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| {b.Id} | {b.Severity} | {b.StageId ?? "-"} | {Cell(b.Title)} | {Cell(filedBy)} |");

    /// <summary>A bug title is free text an agent typed; a pipe in it would silently break the table.</summary>
    private static string Cell(string s) =>
        s.Replace("|", "\\|", StringComparison.Ordinal)
         .Replace("\r", " ", StringComparison.Ordinal)
         .Replace("\n", " ", StringComparison.Ordinal)
         .Trim();
}
