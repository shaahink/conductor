using Conductor.Core;
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
