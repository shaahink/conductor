using System.ComponentModel;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// F1.3: Task/checkpoint CRUD — agents report progress via CLI verbs instead of hand-editing
/// the tracker markdown. Since W1.1 the verbs emit work-graph events (claims with agent
/// provenance) into run.db's event log; the tracker regenerates from that fold (tracker-as-view).
/// </summary>
public sealed class TaskCommand : Command<TaskCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--done <CHECKPOINT>")]
        [Description("Mark a checkpoint as DONE.")]
        public string? Done { get; init; }

        [CommandOption("--in-progress <CHECKPOINT>")]
        [Description("Mark a checkpoint as IN PROGRESS (from TODO only).")]
        public string? InProgress { get; init; }

        [CommandOption("--list")]
        [Description("List all checkpoints from run.db.")]
        public bool List { get; init; }

        [CommandOption("-c|--commit <SHA>")]
        [Description("Commit SHA to attribute (for --done).")]
        public string? Commit { get; init; }

        [CommandOption("-e|--evidence <TEXT>")]
        [Description("Evidence string (for --done).")]
        public string? Evidence { get; init; }

        [CommandOption("--blocked-until <ISO8601>")]
        [Description("This session cannot proceed until the given UTC instant (e.g. 2026-07-31T15:12:00Z). Requires --reason.")]
        public string? BlockedUntil { get; init; }

        [CommandOption("--reason <TEXT>")]
        [Description("Why the run is blocked (for --blocked-until). Required.")]
        public string? Reason { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[red]No run.db found.[/] Run the conductor at least once to initialize the database.");
            return 1;
        }

        try
        {
            using var store = new SqliteRunStore(runDbPath,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
            var runId = store.GetLatestRunId(plan.Name);
            if (string.IsNullOrEmpty(runId))
            {
                AnsiConsole.MarkupLine("[red]No run found in run.db.[/] Initialize the run first.");
                return 1;
            }

            if (settings.BlockedUntil != null)
            {
                // SC5.1: the same rules the run loop will re-apply at verdict time — refused HERE, in
                // front of the agent that can still do something about it, rather than swallowed and
                // silently ignored after the session has exited.
                var (until, error) = BlockedUntilRequest.Parse(settings.BlockedUntil, settings.Reason);
                if (until is not { } untilUtc)
                {
                    AnsiConsole.MarkupLine($"[red]blocked-until refused:[/] {Markup.Escape(error ?? "invalid request")}");
                    return 1;
                }
                var stage = CurrentStage(store, runId);
                store.RequestBlockedUntil(runId, untilUtc, settings.Reason!.Trim(), stage);
                // The reason is knowledge, not just a control signal: it belongs in the ledger the
                // waking session reads, exactly as sk #1's agent wrote its window into lessons.md.
                store.WriteLedger(runId, null, stage, "blocked-until",
                    $"Blocked until {untilUtc:yyyy-MM-dd HH:mm:ss}Z: {settings.Reason!.Trim()}");
                AnsiConsole.MarkupLine(
                    $"[yellow]blocked-until accepted[/] — {Markup.Escape(BlockedUntilRequest.Describe(untilUtc, settings.Reason!.Trim()))}");
                AnsiConsole.MarkupLine("[grey]the run loop will sleep until then and spawn one more session; no attempt is burned. End your session now.[/]");
            }
            else if (settings.Done != null)
            {
                var allCps = store.GetCheckpoints(runId);
                if (!allCps.Any(c => c.Id.Equals(settings.Done, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[red]Checkpoint '{Markup.Escape(settings.Done)}' not found in run.db[/]");
                    return 1;
                }
                store.UpdateCheckpoint(runId, settings.Done, "DONE",
                    settings.Commit ?? "-", settings.Evidence ?? "marked done via CLI", source: "agent");
                AnsiConsole.MarkupLine($"[green]checkpoint {Markup.Escape(settings.Done)} → DONE[/]");
            }
            else if (settings.InProgress != null)
            {
                var allCps = store.GetCheckpoints(runId);
                if (!allCps.Any(c => c.Id.Equals(settings.InProgress, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[red]Checkpoint '{Markup.Escape(settings.InProgress)}' not found in run.db[/]");
                    return 1;
                }
                store.MarkCheckpointInProgress(runId, settings.InProgress);
                AnsiConsole.MarkupLine($"[yellow]checkpoint {Markup.Escape(settings.InProgress)} → IN PROGRESS[/]");
            }
            else if (settings.List)
            {
                var cps = store.GetCheckpoints(runId);

                if (cps.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]no checkpoints in run.db[/]");
                    return 0;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold aqua]Checkpoints from run.db[/]")
                    .AddColumn("Stage")
                    .AddColumn("ID")
                    .AddColumn("Title")
                    .AddColumn("Status");

                foreach (var cp in cps)
                {
                    var icon = cp.Status switch
                    {
                        var s when s.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) => "[green]DONE[/]",
                        var s when s.StartsWith("IN", StringComparison.OrdinalIgnoreCase) => "[yellow]IN PROG[/]",
                        var s when s.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) => "[red]BLOCKED[/]",
                        _ => "[grey]TODO[/]",
                    };
                    table.AddRow(Markup.Escape(cp.StageId), Markup.Escape(cp.Id), Markup.Escape(cp.Title), icon);
                }
                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]Usage: conductor task --list | --done <id> | --in-progress <id> | --blocked-until <iso8601> --reason <text>[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        return 0;
    }

    /// <summary>The stage the run is on, read from persisted run state — the wait is stamped with it
    /// so status and the report can say which stage is waiting, not just that something is.</summary>
    private static string? CurrentStage(SqliteRunStore store, string runId)
    {
        try
        {
            var json = store.LoadRunStateJson(runId);
            if (string.IsNullOrEmpty(json)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)?.CurrentStage;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }
}
