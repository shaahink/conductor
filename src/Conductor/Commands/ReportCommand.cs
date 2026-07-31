using System.ComponentModel;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Planning;
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

        // SC2.4 — offline from run.db. This command used to build the report from state.json and the
        // declared tracker with a NULL store, so every DB-fed section (timeline, execution health, MCP
        // metrics) short-circuited to empty: a report regenerated after the engine exited silently lost
        // the history it exists to show. The engine's own path always passed a store; only the operator
        // running `conductor report` by hand got the hollow one.
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        using var store = File.Exists(runDbPath)
            ? new SqliteRunStore(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance)
            : null;

        var state = LoadState(plan, store);
        // W5.1: the graph's statuses, not the declaration's — same read the run loop schedules on.
        var track = WorkSnapshot.Read(store, state.RunId, () => SafeReadDeclared(plan));

        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null, null,
            Reporter.ReadTimeline(store, state.RunId), Reporter.ReadHealth(store, state.RunId),
            mcp: Reporter.ReadMcpMetrics(store, state.RunId),
            repo: Reporter.ReadRepoStrip(plan)), Reporter.Utf8Bom);
        AnsiConsole.MarkupLine($"report written to [bold]{Markup.Escape(Reporter.ReportPath(plan))}[/]" +
                               (store != null ? " [grey](from run.db)[/]" : " [yellow](no run.db — declared plan only)[/]"));

        // SC2.4: the same closing statement the engine writes on completion, regenerable by hand from
        // the database long after the process that ran the plan is gone.
        if (store != null && !string.IsNullOrEmpty(state.RunId))
        {
            RunSummary.Write(plan, state, track, store, _ => { });
            AnsiConsole.MarkupLine($"run summary written to [bold]{Markup.Escape(RunSummary.SummaryPath(plan))}[/]");
        }
        return 0;
    }

    /// <summary>The run state as the DATABASE has it — <c>run_state</c> is written by the same
    /// <c>RunContext.Save</c> that writes state.json, so it is current to the last save, and it is the
    /// copy that survives a state dir whose state.json was cleaned up or belongs to another plan.
    /// Falls back to state.json when the row is missing (a run.db from before the state table, or a
    /// plan that has never run).</summary>
    private static RunState LoadState(PlanConfig plan, SqliteRunStore? store)
    {
        var fileState = RunState.LoadOrNew(Path.Combine(plan.StateDir, "state.json"), plan.Name);
        if (store == null) return fileState;
        try
        {
            var runId = !string.IsNullOrEmpty(fileState.RunId) ? fileState.RunId : store.GetLatestRunId(plan.Name);
            if (string.IsNullOrEmpty(runId)) return fileState;
            var json = store.LoadRunStateJson(runId);
            if (string.IsNullOrEmpty(json)) return fileState;
            var dbState = JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts);
            if (dbState == null) return fileState;
            if (string.IsNullOrEmpty(dbState.RunId)) dbState.RunId = runId;
            return dbState;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return fileState;
        }
    }

    private static TrackerSnapshot SafeReadDeclared(PlanConfig plan)
    {
        try { return ProgressProviderFactory.Create(plan).Read(plan, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new TrackerSnapshot(); }
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
