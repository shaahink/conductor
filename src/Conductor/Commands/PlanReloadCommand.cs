using System.Text.Json;

using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// `conductor plan reload`: re-read the full plan JSON from disk, validate it, report — and (G3.2)
/// queue a `reload-plan` control verb so a running orchestrator actually swaps the live plan in at
/// its next session boundary (this used to be validate-and-print only; the "picks up changes"
/// wording was aspirational until the live reload landed).
/// </summary>
public static class PlanReloadCommand
{
    public static int ExecuteReload(string planPath)
    {
        try
        {
            if (!File.Exists(planPath))
            {
                AnsiConsole.MarkupLine($"[red]Plan file not found: {Markup.Escape(planPath)}[/]");
                return 1;
            }

            var plan = PlanConfig.Load(planPath);
            var stageCount = plan.Stages.Count;
            var gateCount = plan.Gates.Count;
            var table = new Table().Border(TableBorder.Rounded).Title("[bold aqua]plan reloaded[/]");
            table.AddColumn("field"); table.AddColumn("value");
            table.AddRow("name", Markup.Escape(plan.Name));
            table.AddRow("version", plan.Version);
            table.AddRow("planVersion", plan.PlanVersion.ToString());
            table.AddRow("repo", Markup.Escape(plan.Repo));
            table.AddRow("stages", stageCount.ToString());
            table.AddRow("gates", gateCount.ToString());
            table.AddRow("gatePolicy", plan.GatePolicy);
            table.AddRow("gate (fast tier)", plan.Gates.Count(g => g.IsFast).ToString());
            table.AddRow("limits.stallMinutes", plan.Limits.StallMinutes.ToString());
            table.AddRow("limits.sessionTimeoutMinutes", plan.Limits.SessionTimeoutMinutes.ToString());
            if (plan.Limits.MaxRunCostUsd is { } cap) table.AddRow("limits.maxRunCostUsd", $"${cap:0.00}");
            if (plan.Limits.MaxRunTokens is { } tok) table.AddRow("limits.maxRunTokens", $"{tok / 1000}K");
            table.AddRow("agent.command", plan.Agent.Command);
            table.AddRow("report.heartbeatMinutes", plan.Report.HeartbeatMinutes.ToString());
            table.AddRow("statusAgent.enabled", plan.StatusAgent?.Enabled.ToString() ?? "false");
            if (plan.ReadOrder is { Count: > 0 }) table.AddRow("readOrder", string.Join(", ", plan.ReadOrder));
            AnsiConsole.Write(table);

            AnsiConsole.MarkupLine($"[green]Plan validated — {stageCount} stages, {gateCount} gates, v{plan.PlanVersion}.[/]");

            // Only a valid plan gets queued: the run loop re-validates on its side too, but there is
            // no point signalling a reload we already know would be skipped.
            Directory.CreateDirectory(plan.StateDir);
            File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
                JsonSerializer.Serialize(new { command = "reload-plan", issuedUtc = DateTime.UtcNow }));
            AnsiConsole.MarkupLine("[grey]reload-plan queued — a running conductor swaps the live plan at its next session boundary.[/]");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]Plan validation failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
