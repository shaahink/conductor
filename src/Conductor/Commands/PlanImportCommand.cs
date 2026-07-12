using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// F7.1: Plan import — an LLM pass (advisor model) converts a natural-language mega-plan description
/// into structured stages, gates, and checkpoints in the plan JSON. Usage: conductor plan import
/// <description-file.md|"free-text description">
/// </summary>
public static class PlanImportCommand
{
    public static int ExecuteImport(string planPath, string? descriptionOrFile)
    {
        if (string.IsNullOrWhiteSpace(descriptionOrFile))
        {
            AnsiConsole.MarkupLine("[red]plan import requires a description (file path or quoted text).[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor plan import ./MEGA-PLAN.md[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor plan import \"deliver a REST API — stage 1: auth, stage 2: endpoints\"[/]");
            return 1;
        }

        try
        {
            var plan = PlanConfig.Load(planPath);
            if (plan.Advisor is not { Enabled: true } || string.IsNullOrWhiteSpace(plan.Advisor.Command))
            {
                AnsiConsole.MarkupLine("[red]Advisor model is not configured. Set advisor.enabled, advisor.command, and advisor.args in your plan.[/]");
                return 1;
            }

            var description = descriptionOrFile;
            // If the argument looks like a file path and exists, read it
            if (File.Exists(descriptionOrFile))
            {
                description = File.ReadAllText(descriptionOrFile, System.Text.Encoding.UTF8);
                AnsiConsole.MarkupLine($"[grey]Read description from {Markup.Escape(descriptionOrFile)} ({description.Length} chars)[/]");
            }

            AnsiConsole.MarkupLine("[grey]Consulting advisor model to generate task graph...[/]");

            var result = PlanImportService.ImportAsync(plan, description, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"))
                .GetAwaiter().GetResult();

            if (result == null)
            {
                AnsiConsole.MarkupLine("[red]Plan import failed — the advisor model could not generate a valid task graph.[/]");
                AnsiConsole.MarkupLine("[grey]Check that the advisor command is working (try: conductor chat \"hello\") and that the description is clear.[/]");
                return 1;
            }

            // Show a preview
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold aqua]Generated plan:[/] {result.Stages.Count} stages, {result.Gates.Count} gates");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Id");
            table.AddColumn("Title");
            table.AddColumn("Sessions");
            table.AddColumn("Kind");
            table.AddColumn("Depends On");
            foreach (var stage in result.Stages)
            {
                table.AddRow(
                    Markup.Escape(stage.Id),
                    Markup.Escape(stage.Title ?? ""),
                    stage.Sessions.ToString(),
                    stage.Kind ?? "deliver",
                    stage.DependsOn is { Count: > 0 } ? Markup.Escape(string.Join(", ", stage.DependsOn)) : "-");
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            if (result.Gates.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Gates:[/]");
                foreach (var gate in result.Gates)
                    AnsiConsole.MarkupLine($"  {Markup.Escape(gate.Name)}: {Markup.Escape(gate.Command ?? "")} (tier={gate.Tier})");
            }

            // Confirm
            if (!AnsiConsole.Confirm("[yellow]Apply these stages and gates to the plan?[/]", false))
            {
                AnsiConsole.MarkupLine("[grey]Import cancelled.[/]");
                return 0;
            }

            PlanImportService.ApplyToPlan(plan, result);
            AnsiConsole.MarkupLine($"[green]Plan updated:[/] {result.Stages.Count} stages, {result.Gates.Count} gates added/merged");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
