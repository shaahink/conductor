using System.Text.Json;

using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// P1 — `conductor plan add-stage <json>`: append a new stage with checkpoints to the plan.
/// The stage JSON is provided inline or piped via stdin; it is validated against the StageConfig schema
/// before being appended. The plan's version is bumped automatically.
/// Examples:
///   conductor plan add-stage "{\"id\":\"P9\",\"title\":\"New phase\",\"sessions\":2}"
/// </summary>
public static class PlanAddStageCommand
{
    public static int ExecuteAddStage(string planPath, PlanCommand.Settings settings)
    {
        // The Value field is the 3rd positional arg, but for add-stage we interpret the remaining args
        // as raw JSON. The Settings model puts KEY as the 2nd arg and VALUE as the 3rd, so
        // add-stage's JSON is in settings.Key (since verb=add-stage, the next arg is JSON).
        var json = settings.Key;
        if (string.IsNullOrWhiteSpace(json))
        {
            // Try reading from stdin (piped input)
            try
            {
                if (Console.IsInputRedirected)
                    json = Console.In.ReadToEnd();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(json))
            {
                AnsiConsole.MarkupLine("[red]plan add-stage requires a JSON stage definition.[/]");
                AnsiConsole.MarkupLine("Example: conductor plan add-stage \"{\\\"id\\\":\\\"P9\\\",\\\"title\\\":\\\"New phase\\\",\\\"sessions\\\":2}\"");
                return 1;
            }
        }

        try
        {
            var plan = PlanConfig.Load(planPath);

            var stage = System.Text.Json.JsonSerializer.Deserialize<StageConfig>(json, PlanConfig.JsonOpts)
                ?? throw new InvalidOperationException("Stage JSON deserialised to null.");

            // Validate the stage
            if (string.IsNullOrWhiteSpace(stage.Id))
                throw new InvalidOperationException("stage.id is required.");
            if (string.IsNullOrWhiteSpace(stage.Title))
                throw new InvalidOperationException("stage.title is required.");
            if (plan.Stages.Any(s => s.Id.Equals(stage.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"stage '{stage.Id}' already exists in the plan.");

            plan.AddStage(stage);
            plan.Save();

            AnsiConsole.MarkupLine($"[green]stage added[/] → [bold]{Markup.Escape(stage.Id)}[/] [grey]{Markup.Escape(stage.Title)}[/] (plan v{plan.PlanVersion})");

            // W1.2: sync the work graph so the new stage is schedulable and on the board — no more
            // "don't forget the tracker" (the tracker is a generated view of the graph now).
            var runDbPath = Path.Combine(plan.StateDir, "run.db");
            if (File.Exists(runDbPath))
            {
                using var store = new Core.Store.SqliteRunStore(runDbPath,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Store.SqliteRunStore>.Instance);
                if (store.GetLatestRunId(plan.Name) is { Length: > 0 } runId)
                {
                    Core.Planning.WorkGraphSync.Sync(plan, store, runId,
                        msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]No run.db yet — the stage's work item is scaffolded at the next run start.[/]");
            }
            AnsiConsole.MarkupLine($"[grey]Total stages now: {plan.Stages.Count}.[/]");
            return 0;
        }
        catch (System.Text.Json.JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Invalid stage JSON: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
