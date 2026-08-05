using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Commands;

/// <summary>
/// The two doctor checks that go to the run store — split out under the K2.3 file convention when
/// K3.1 pushed <c>DoctorCommand.cs</c> over its 500-line ceiling. They belong together: both answer
/// questions about a database that, since K3.1, is no longer in the working tree.
/// </summary>
public sealed partial class DoctorCommand
{
    /// <summary>K3.1: "where did my history go, and why there". The store is no longer sitting in
    /// the working tree next to the logs, so the resolution has to be readable — which rule won, the
    /// path it produced, and whether this resolution imported a pre-K3.1 database.</summary>
    private static Check CheckState(PlanConfig plan)
    {
        var r = plan.ResolveState();
        var how = r.Source switch
        {
            StateSource.EnvOverride => $"{StateHome.RunDbEnvVar} override",
            StateSource.Pointer => $"{StateHome.ScratchDirName}/{StateHome.PointerFileName}",
            _ => "state home",
        };
        var imported = r.Import ?? StateMigration.ReadReceipt(r.RunDbPath);
        var suffix = imported is null ? "" : $"; imported from {imported.From}";
        return File.Exists(r.RunDbPath)
            ? new Check("state", "ok", $"{r.RunDbPath} (via {how}){suffix}")
            : new Check("state", "ok", $"{r.RunDbPath} (via {how}) — no history yet, the first run creates it");
    }

    /// <summary>
    /// K4.2 — the token ceiling against what this repo's own sessions measured. Two failures are
    /// worth catching before a run starts, and both have already happened here: a cap BELOW the
    /// session floor, where nothing can land in one session and the total rises (one client run was
    /// worse than uncapped for three stages), and a nudge below the median session that finishes,
    /// where the rail interrupts every session before it could have ended naturally — the subtler one,
    /// and the one this repo shipped.
    /// <para>Read-only throughout: <see cref="RunArchive"/> opens the database in SQLite's read-only
    /// mode, so a doctor run against a live run cannot migrate or disturb it.</para>
    /// </summary>
    internal static Check CheckTokenBudget(PlanConfig plan)
    {
        var cap = plan.Limits.MaxSessionTokens is { } c and > 0 ? c : (long?)null;
        if (cap is null)
            return new Check("tokens", "ok", "no session ceiling — sessions run until the agent stops");

        // The same fallback the rail itself applies (SessionRunner.Mcp.cs:120): an unset ratio nudges
        // at 0.8, so reading it as "no nudge" would describe a rail that does fire as one that does not.
        var ratio = plan.Limits.SoftBreakRatio is { } r and > 0 and <= 1.0 ? r : 0.8;
        var nudge = (long)(cap.Value * ratio);
        var configured = $"cap {BudgetAnalyzer.Millions(cap.Value)} / nudge {BudgetAnalyzer.Millions(nudge)}";

        var archive = RunArchive.TryOpen(plan.ResolveState().RunDbPath);
        if (archive is null)
            return new Check("tokens", "ok", $"{configured} — no history yet to measure it against");

        BudgetProfile? measured = null;
        // Oldest first, and a run of THIS plan beats an unrelated one sharing the database: the check
        // must describe the budget these sessions will run under, not whatever ran here last year.
        var runs = archive.Runs()
            .OrderBy(r => string.Equals(r.PlanName, plan.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(r => r.StartedUtc, StringComparer.Ordinal)
            .ToList();
        foreach (var run in runs)
        {
            var sessions = archive.Sessions(run.RunId);
            if (sessions.Count == 0) continue;
            var profile = BudgetAnalyzer.Analyze(run.RunId, run.PlanName, sessions, archive.SoftBreaks(run.RunId));
            if (profile.Current.Closers > 0) measured = profile;
        }
        if (measured is null)
            return new Check("tokens", "ok", $"{configured} — no session has closed a checkpoint yet, so there is no floor to check it against");

        var w = measured.Current;
        if (cap.Value < w.Floor)
            return new Check("tokens", "warn",
                $"{configured} — the cap is BELOW the measured {BudgetAnalyzer.Millions(w.Floor)} session floor. " +
                $"Nothing will land in one session. {measured.Prescription.Verdict}");
        if (nudge < w.ClosingMedian)
            return new Check("tokens", "warn",
                $"{configured} — the nudge is {(nudge / (double)w.ClosingMedian).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}x " +
                $"the {BudgetAnalyzer.Millions(w.ClosingMedian)} median closing session, so it fires before a typical session could have finished. " +
                measured.Prescription.Verdict);
        return new Check("tokens", "ok",
            $"{configured} — clears the {BudgetAnalyzer.Millions(w.Floor)} floor and the " +
            $"{BudgetAnalyzer.Millions(w.ClosingMedian)} median closing session ({w.Closers} measured). conductor budget for the full profile");
    }

    private static (decimal CostUsd, bool HasRun) TryReadCostFromRunDb(PlanConfig plan)
    {
        var runDbPath = plan.RunDbPath;
        if (!File.Exists(runDbPath)) return (0m, false);
        try
        {
            using var store = new SqliteRunStore(runDbPath, NullLogger<SqliteRunStore>.Instance);
            var report = StatusReportBuilder.Build(plan, store);
            return (report.TotalCostUsd, report.Kind != "norun");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return (0m, false);
        }
    }
}
