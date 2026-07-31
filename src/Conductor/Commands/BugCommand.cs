using System.ComponentModel;
using System.Globalization;

using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// M7.2: tracked bugs. A found bug becomes a row in run.db that OUTLIVES the session that found it,
/// is injected into later session prompts (<see cref="Core.BugsBattery"/>), and feeds the audit phase —
/// so agents stop re-finding the same bug. Sub-commands: <c>new</c> · <c>list</c> · <c>fix</c>.
/// </summary>
public sealed class BugCommand : Command<BugCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: new, list, fix. Omit to show help.")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[TITLE_OR_ID]")]
        [Description("Bug title (new) or bug id (fix).")]
        public string? TitleOrId { get; init; }

        [CommandOption("-d|--detail <TEXT>")]
        [Description("Longer description / repro (new only).")]
        public string? Detail { get; init; }

        [CommandOption("-s|--severity <SEVERITY>")]
        [Description("low | medium | high (new only). Default: medium.")]
        public string? Severity { get; init; }

        [CommandOption("--stage <STAGE>")]
        [Description("Stage id to associate the bug with (e.g. M7). Optional.")]
        public string? Stage { get; init; }

        [CommandOption("--all")]
        [Description("list: show fixed/wontfix bugs too (default shows only open).")]
        public bool All { get; init; }

        [CommandOption("--wontfix")]
        [Description("fix: close the bug as wontfix instead of fixed.")]
        public bool WontFix { get; init; }
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

        var verb = settings.Verb.ToLowerInvariant();
        if (verb is "" or "help" or "-h" or "--help")
            return PrintBugHelp();

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
            var state = LoadState(store, plan, runId);

            return verb switch
            {
                "new" or "add" or "file" => New(store, plan, state, settings),
                "list" or "ls" => List(store, runId, settings.All),
                "fix" or "close" or "resolve" => Fix(store, state, settings),
                _ => PrintBugHelp(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            AnsiConsole.MarkupLine($"[red]bug command failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static RunState LoadState(SqliteRunStore store, PlanConfig plan, string runId)
    {
        var json = store.LoadRunStateJson(runId);
        return string.IsNullOrEmpty(json)
            ? new RunState { PlanName = plan.Name, RunId = runId }
            : System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)
              ?? new RunState { PlanName = plan.Name, RunId = runId };
    }

    private static int New(SqliteRunStore store, PlanConfig plan, RunState state, Settings settings)
    {
        var title = settings.TitleOrId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            AnsiConsole.MarkupLine("[red]bug new needs a title:[/] conductor bug new \"<title>\" [[--detail <text>]] [[--severity high]]");
            return 1;
        }
        var stageId = settings.Stage ?? state.CurrentStage;
        var foundSession = state.SessionCounter > 0 ? (int?)state.SessionCounter : null;
        var id = store.WriteBug(state.RunId, title, settings.Detail, settings.Severity ?? "medium", stageId, foundSession);
        if (id <= 0)
        {
            AnsiConsole.MarkupLine("[red]bug write failed[/] (see run.db error log).");
            return 1;
        }
        AnsiConsole.MarkupLine($"[green]bug #{id} filed[/] ({Markup.Escape(settings.Severity ?? "medium")}): {Markup.Escape(title)}");
        return 0;
    }

    /// <summary>SF0.4: lists this run's bugs AND the open ones earlier runs in this repo left behind.
    /// The carried rows are the whole point — before this the ledger was silently reset by every new
    /// run, so eleven open bugs became an empty list that read like a clean bill of health.</summary>
    private static int List(SqliteRunStore store, string runId, bool all)
    {
        var bugs = store.QueryBugs(runId, status: all ? null : "open");
        var carried = store.QueryCarriedBugs(runId);
        if (bugs.Count == 0 && carried.Count == 0)
        {
            AnsiConsole.MarkupLine(all ? "[grey]No bugs filed.[/]" : "[grey]No open bugs.[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("sev");
        table.AddColumn("status");
        table.AddColumn("stage");
        table.AddColumn("title");
        // Only when there is something to attribute — an extra column on every run would cost width
        // for a value that is "this run" on every row.
        if (carried.Count > 0) table.AddColumn("filed by");

        foreach (var b in bugs)
            AddRow(table, b, carried.Count > 0 ? "this run" : null);
        foreach (var c in carried)
            AddRow(table, c.Bug, $"[yellow]{Markup.Escape(ShortPlan(c.PlanName))}[/]");

        AnsiConsole.Write(table);
        if (carried.Count > 0)
            AnsiConsole.MarkupLine(
                $"[grey]{carried.Count} open bug(s) carried forward from an earlier run in this repo. " +
                "They are still yours: fix and close them with [/][yellow]conductor bug fix <id>[/][grey].[/]");
        return 0;
    }

    private static void AddRow(Table table, BugRow b, string? filedBy)
    {
        var sevColor = b.Severity switch { "high" => "red", "low" => "grey", _ => "yellow" };
        var statusColor = b.Status switch { "open" => "yellow", "fixed" => "green", _ => "grey" };
        var cells = new List<string>
        {
            b.Id.ToString(CultureInfo.InvariantCulture),
            $"[{sevColor}]{Markup.Escape(b.Severity)}[/]",
            $"[{statusColor}]{Markup.Escape(b.Status)}[/]",
            Markup.Escape(b.StageId ?? "-"),
            Markup.Escape(b.Title),
        };
        if (filedBy != null) cells.Add(filedBy);
        table.AddRow(cells.ToArray());
    }

    /// <summary>Plan names are sentences ("Sarban core - the engine says what it knows"); the column
    /// only needs enough to tell one run from another.</summary>
    private static string ShortPlan(string planName) =>
        string.IsNullOrWhiteSpace(planName) ? "an earlier run"
        : planName.Length <= 24 ? planName
        : planName[..23].TrimEnd() + "…";

    private static int Fix(SqliteRunStore store, RunState state, Settings settings)
    {
        if (!long.TryParse(settings.TitleOrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            AnsiConsole.MarkupLine("[red]bug fix needs a numeric id:[/] conductor bug fix <id> [[--wontfix]]");
            return 1;
        }
        var status = settings.WontFix ? "wontfix" : "fixed";
        var fixedSession = state.SessionCounter > 0 ? (int?)state.SessionCounter : null;
        // SF0.4: no longer "for this run" — a bug an earlier run filed is closable here, which is the
        // only way carrying it forward means anything.
        if (!store.UpdateBugStatus(state.RunId, id, status, fixedSession))
        {
            AnsiConsole.MarkupLine($"[red]no bug #{id} in this repo's run.db[/] (wrong id — try [yellow]conductor bug list --all[/]).");
            return 1;
        }
        AnsiConsole.MarkupLine($"[green]bug #{id} → {status}[/]");
        return 0;
    }

    private static int PrintBugHelp()
    {
        AnsiConsole.MarkupLine("[bold aqua]conductor bug[/] — tracked bugs that outlive the session that found them");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]new[/]  [grey]File a bug:[/] conductor bug new \"<title>\" [[--detail <text>]] [[--severity low|medium|high]] [[--stage <id>]]");
        AnsiConsole.MarkupLine("  [bold]list[/] [grey]List open bugs (or --all for every bug)[/]");
        AnsiConsole.MarkupLine("  [bold]fix[/]  [grey]Close a bug:[/] conductor bug fix <id> [[--wontfix]]");
        return 0;
    }
}
