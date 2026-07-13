using System.ComponentModel;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class ReportCommand : Command<ReportCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--query <SQL>")]
        [Description("F1.4: Run an ad-hoc SQL query against run.db instead of generating REPORT.md. " +
                     "Example: --query \"SELECT stage_id, SUM(cost_usd) FROM costs GROUP BY stage_id\"")]
        public string? Query { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());

        if (settings.Query != null)
        {
            return RunQuery(plan, settings.Query);
        }

        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null, null,
            Reporter.ReadTimeline(null, state.RunId), Reporter.ReadHealth(null, state.RunId),
            mcp: Reporter.ReadMcpMetrics(null, state.RunId),
            repo: Reporter.ReadRepoStrip(plan)), Reporter.Utf8Bom);
        AnsiConsole.MarkupLine($"report written to [bold]{Markup.Escape(Reporter.ReportPath(plan))}[/]");
        return 0;
    }

    private static int RunQuery(PlanConfig plan, string sql)
    {
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[red]No run.db found.[/] Run the conductor at least once to initialize the database.");
            return 1;
        }

        try
        {
            using var db = new SqliteRunStore(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
            var rows = db.Query(sql);

            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]no rows returned[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold aqua]Query result[/] ({rows.Count} row{(rows.Count == 1 ? "" : "s")})");

            var columns = rows[0].Keys;
            foreach (var col in columns)
                table.AddColumn(Markup.Escape(col));

            foreach (var row in rows)
            {
                var values = columns.Select(c =>
                {
                    var v = row.GetValueOrDefault(c);
                    return v switch
                    {
                        null => "[grey]-[/]",
                        double d => d.ToString("F4"),
                        float f => f.ToString("F4"),
                        long l => l.ToString(),
                        int i => i.ToString(),
                        _ => Markup.Escape(v.ToString()!)
                    };
                }).ToArray();
                table.AddRow(values);
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Query failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
