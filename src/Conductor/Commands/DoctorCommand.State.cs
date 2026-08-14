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
    /// <para>KS5.3: the comparison itself lives in <see cref="BudgetDisagreement"/> now, because the
    /// plan reload has to make the same one at the session boundary. What is left here is doctor's
    /// half — which database to open and which run in it to measure — and the translation of one
    /// verdict into one check. Two copies of "is this ceiling under the floor" would be two answers
    /// the first time either was edited.</para>
    /// </summary>
    internal static Check CheckTokenBudget(PlanConfig plan)
    {
        var archive = RunArchive.TryOpen(plan.ResolveState().RunDbPath);
        var verdict = BudgetDisagreement.Compare(
            plan.Limits.MaxSessionTokens, plan.Limits.SoftBreakRatio,
            BudgetDisagreement.MeasureForPlan(archive, plan.Name),
            measurable: archive is not null);
        return new Check("tokens", verdict.DoctorState, verdict.Sentence);
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
