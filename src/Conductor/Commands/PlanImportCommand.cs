using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// M6.1/M6.2 (was F7.1): Plan import. A <b>structured</b> markdown plan/tracker document is parsed
/// deterministically into stages with no model call (zero spend); freeform prose falls back to the
/// advisor model (<c>--model</c> picks it). The result is diffed against the current plan and only the
/// added/changed stages+gates are applied — a re-import never clobbers hand-tuned entries (M6.2).
/// Usage: <c>conductor plan import &lt;file.md|"free text"&gt; [--model X] [--yes]</c>
/// </summary>
public static class PlanImportCommand
{
    public static int ExecuteImport(string planPath, string? descriptionOrFile, string? model = null, bool assumeYes = false)
    {
        if (string.IsNullOrWhiteSpace(descriptionOrFile))
        {
            AnsiConsole.MarkupLine("[red]plan import requires a description (file path or quoted text).[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor plan import ./docs/history/MAESTRO-PLAN.md[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor plan import \"deliver a REST API — stage 1: auth, stage 2: endpoints\"[/]");
            return 1;
        }

        try
        {
            var plan = PlanConfig.Load(planPath);

            var description = descriptionOrFile;
            if (File.Exists(descriptionOrFile))
            {
                description = File.ReadAllText(descriptionOrFile, System.Text.Encoding.UTF8);
                AnsiConsole.MarkupLine($"[grey]Read {Markup.Escape(descriptionOrFile)} ({description.Length} chars)[/]");
            }

            // M6.1: prefer the deterministic path — no model, no spend. KS3.5: which now covers three
            // foreign formats as well as this project's own, selected by content.
            var (result, format) = PlanImportService.ParseKnown(description, plan);
            if (result is not null)
            {
                AnsiConsole.MarkupLine($"[grey]Read {Markup.Escape(ImportBridge.Describe(format))} deterministically " +
                    $"(no model call) → {result.Stages.Count} stages, {result.Checkpoints.Count} checkpoints[/]");
            }
            else
            {
                if (plan.Advisor is not { Enabled: true } || string.IsNullOrWhiteSpace(plan.Advisor.Command))
                {
                    AnsiConsole.MarkupLine("[red]This text isn't a structured plan, and no advisor model is configured to interpret it.[/]");
                    AnsiConsole.MarkupLine("[grey]Either pass a structured plan/tracker markdown file, or set advisor.enabled/command/args in the plan.[/]");
                    return 1;
                }
                AnsiConsole.MarkupLine($"[grey]Freeform text — consulting the advisor model{(model is null ? "" : $" ({Markup.Escape(model)})")}…[/]");
                result = PlanImportService.ImportAsync(plan, description, model,
                        msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"))
                    .GetAwaiter().GetResult();
            }

            if (result is null)
            {
                AnsiConsole.MarkupLine("[red]Plan import failed — could not derive a task graph.[/]");
                return 1;
            }

            // M6.2: diff against the current plan; show exactly what would change.
            var diff = PlanDiff.Compute(plan, result);
            RenderDiff(diff);

            if (diff.IsEmpty)
            {
                AnsiConsole.MarkupLine("[green]Nothing to change — the plan already matches this import.[/]");
                return 0;
            }

            if (!assumeYes && !AnsiConsole.Confirm($"[yellow]Apply {diff.TotalChanges} change(s) to the plan?[/]", false))
            {
                AnsiConsole.MarkupLine("[grey]Import cancelled — the plan was not modified.[/]");
                return 0;
            }

            diff.Apply(plan);
            AnsiConsole.MarkupLine($"[green]Plan updated[/] — {diff.AddedStages.Count} stage(s) added, {diff.ChangedStages.Count} changed, " +
                $"{diff.AddedGates.Count} gate(s) added, {diff.ChangedGates.Count} changed, " +
                $"{diff.AddedCheckpoints.Count} checkpoint(s) declared. Now v{plan.PlanVersion}.");
            AnsiConsole.MarkupLine("[grey]A running conductor picks up the change at its next session boundary.[/]");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static void RenderDiff(PlanDiff diff)
    {
        AnsiConsole.WriteLine();
        if (diff.AddedStages.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[green]+ new stages[/]");
            table.AddColumn("id"); table.AddColumn("title"); table.AddColumn("sessions"); table.AddColumn("dependsOn");
            foreach (var s in diff.AddedStages)
                table.AddRow(Markup.Escape(s.Id), Markup.Escape(s.Title ?? ""), s.Sessions.ToString(),
                    s.DependsOn is { Count: > 0 } ? Markup.Escape(string.Join(", ", s.DependsOn)) : "-");
            AnsiConsole.Write(table);
        }

        if (diff.ChangedStages.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[yellow]~ changed stages[/]");
            table.AddColumn("id"); table.AddColumn("field"); table.AddColumn("old"); table.AddColumn("new");
            foreach (var ch in diff.ChangedStages)
                foreach (var f in ch.Fields)
                    table.AddRow(Markup.Escape(ch.Id), Markup.Escape(f.Field),
                        Markup.Escape(f.Old ?? "-"), $"[green]{Markup.Escape(f.New ?? "-")}[/]");
            AnsiConsole.Write(table);
        }

        if (diff.AddedGates.Count > 0)
        {
            AnsiConsole.MarkupLine("[green]+ new gates:[/]");
            foreach (var g in diff.AddedGates)
                AnsiConsole.MarkupLine($"  {Markup.Escape(g.Name)}: {Markup.Escape(g.Command ?? "")} (tier={Markup.Escape(g.Tier)})");
        }

        if (diff.AddedCheckpoints.Count > 0)
        {
            // W4.1: the work itself — what an imported plan used to arrive without.
            var table = new Table().Border(TableBorder.Rounded).Title("[green]+ declared work[/]");
            table.AddColumn("id"); table.AddColumn("checkpoint"); table.AddColumn("status");
            foreach (var c in diff.AddedCheckpoints)
                table.AddRow(Markup.Escape(c.Id), Markup.Escape(c.Title), Markup.Escape(c.Status ?? "TODO"));
            AnsiConsole.Write(table);
        }

        if (diff.ChangedGates.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]~ changed gates:[/]");
            foreach (var ch in diff.ChangedGates)
                foreach (var f in ch.Fields)
                    AnsiConsole.MarkupLine($"  {Markup.Escape(ch.Name)}.{Markup.Escape(f.Field)}: {Markup.Escape(f.Old ?? "-")} → [green]{Markup.Escape(f.New ?? "-")}[/]");
        }
        AnsiConsole.WriteLine();
    }
}
