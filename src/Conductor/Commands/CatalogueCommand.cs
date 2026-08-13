using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Store;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS0.1 — <c>conductor catalogue</c>. The state home is an index plus one database per (repo, plan),
/// and until this checkpoint the import that fills it keyed on the plan slug: a new plan in an old
/// repo copied that repo's whole history in again, and every run in it was listed twice. The import
/// no longer does that (<see cref="StateDedup"/>); this verb is how the damage already on disk gets
/// undone, and how an operator sees the shape of their own store.
/// <para><c>catalogue</c> lists. <c>catalogue repair</c> says what it would collapse and writes
/// nothing. <c>catalogue repair --apply</c> backs up every store it will touch and then collapses
/// them. Nothing here writes a store a live engine is using — see <see cref="StateRepair"/>.</para>
/// </summary>
public sealed class CatalogueCommand : Command<CatalogueCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[verb]")]
        [Description("list (the default), or repair.")]
        public string? Verb { get; init; }

        [CommandOption("--apply")]
        [Description("repair only: actually collapse the duplicates. Without it, nothing is written.")]
        public bool Apply { get; init; }

        [CommandOption("--home <PATH>")]
        [Description("Read a state home other than this machine's.")]
        public string? Home { get; init; }

        [CommandOption("--json")]
        [Description("Machine-readable output.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = string.IsNullOrWhiteSpace(settings.Home) ? StateHome.Root : Path.GetFullPath(settings.Home);
        var verb = (settings.Verb ?? "list").ToLowerInvariant();

        return verb switch
        {
            "" or "list" or "ls" => List(root, settings),
            "repair" or "dedup" => Repair(root, settings),
            _ => Help(verb),
        };
    }

    private static int Help(string verb)
    {
        AnsiConsole.MarkupLine($"[red]unknown catalogue verb '{Markup.Escape(verb)}'.[/]");
        AnsiConsole.MarkupLine("[grey]conductor catalogue[/]                 every run store this machine has");
        AnsiConsole.MarkupLine("[grey]conductor catalogue repair[/]          what is duplicated, and what would be collapsed");
        AnsiConsole.MarkupLine("[grey]conductor catalogue repair --apply[/]  collapse it, after backing every store up");
        return 2;
    }

    // ------------------------------------------------------------------------------- list

    private static int List(string root, Settings settings)
    {
        var plan = StateRepair.Survey(root);
        if (settings.Json) return WriteJson(plan, null);

        if (plan.Stores.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]no run stores[/] under [grey]{Markup.Escape(root)}[/].");
            return 0;
        }

        var duplicated = plan.Duplicates.Select(d => d.RunId).ToHashSet(StringComparer.Ordinal);
        var table = new Table().Border(TableBorder.Rounded).Title($"Run stores in {Markup.Escape(root)}");
        table.AddColumn("Store");
        table.AddColumn("Plan");
        table.AddColumn(new TableColumn("Runs").RightAligned());
        table.AddColumn("");

        foreach (var s in plan.Stores.OrderByDescending(s => s.FirstSeenUtc))
        {
            var dup = s.Runs.Count(r => duplicated.Contains(r.RunId));
            table.AddRow(
                Markup.Escape(s.Slug),
                Markup.Escape(s.Plan),
                s.Runs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (s.Live ? "[green]live[/] " : "") + (dup > 0 ? $"[yellow]{dup} duplicated[/]" : ""));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(plan.RunRows == plan.DistinctRuns
            ? $"[green]{plan.DistinctRuns} runs, one row each.[/]"
            : $"[yellow]{plan.RunRows} rows for {plan.DistinctRuns} real runs[/] - "
              + $"{plan.Duplicates.Count} run(s) are in more than one store. [grey]conductor catalogue repair[/]");
        return 0;
    }

    // ----------------------------------------------------------------------------- repair

    private static int Repair(string root, Settings settings)
    {
        var plan = StateRepair.Survey(root);

        if (plan.Duplicates.Count == 0)
        {
            if (settings.Json) return WriteJson(plan, null);
            AnsiConsole.MarkupLine($"[green]nothing duplicated[/] - {plan.DistinctRuns} runs in "
                                   + $"{plan.Stores.Count} stores, one row each. nothing to repair.");
            foreach (var d in plan.Deferred) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(d)}[/]");
            return 0;
        }

        if (!settings.Json)
        {
            AnsiConsole.MarkupLine($"[yellow]{plan.RunRows} run rows for {plan.DistinctRuns} real runs[/] "
                                   + $"in {plan.Stores.Count} stores under [grey]{Markup.Escape(root)}[/].");
            foreach (var d in plan.Duplicates)
            {
                AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(Short(d.RunId))}[/] "
                                       + $"{Markup.Escape(d.PlanName)} - in {d.RemoveFrom.Count + 1} stores");
                AnsiConsole.MarkupLine($"    keep   [green]{Markup.Escape(StoreName(d.OwnerDb))}[/] "
                                       + $"[grey]({Markup.Escape(d.OwnerReason)})[/]");
                foreach (var r in d.RemoveFrom)
                    AnsiConsole.MarkupLine($"    remove [red]{Markup.Escape(StoreName(r))}[/]");
            }
            foreach (var d in plan.Deferred) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(d)}[/]");
        }

        if (!settings.Apply)
        {
            if (settings.Json) return WriteJson(plan, null);
            AnsiConsole.MarkupLine("[grey]nothing was written. re-run with --apply to collapse them; "
                                   + "every store touched is backed up first.[/]");
            return 0;
        }

        RepairOutcome outcome;
        try
        {
            outcome = StateRepair.Apply(root, plan, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]repair stopped:[/] {Markup.Escape(e.Message)}");
            return 1;
        }

        var after = StateRepair.Survey(root);
        if (settings.Json) return WriteJson(after, outcome);

        AnsiConsole.MarkupLine($"[green]backed up[/] to [grey]{Markup.Escape(outcome.BackupDir)}[/] "
                               + $"({outcome.StoresChanged.Count} store(s)) [grey]before writing anything[/]");
        foreach (var n in outcome.Notes) AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(n)}[/]");
        AnsiConsole.MarkupLine($"[green]removed {outcome.RowsDeleted} rows[/]; the catalogue now holds "
                               + $"{after.RunRows} rows for {after.DistinctRuns} runs.");
        return after.RunRows == after.DistinctRuns ? 0 : 1;
    }

    // ------------------------------------------------------------------------------ shape

    private static string Short(string runId) => runId[..Math.Min(8, runId.Length)];

    private static string StoreName(string db) => Path.GetFileName(Path.GetDirectoryName(db)!) ?? db;

    private static int WriteJson(RepairPlan plan, RepairOutcome? outcome)
    {
        var payload = new CatalogueJson(
            plan.Root,
            plan.Stores.Count,
            plan.RunRows,
            plan.DistinctRuns,
            plan.Duplicates.Select(d => new DuplicateJson(
                d.RunId, d.PlanName, d.OwnerDb, d.OwnerReason, d.RemoveFrom.ToList())).ToList(),
            plan.Deferred.ToList(),
            outcome is not null,
            outcome?.BackupDir,
            outcome?.RowsDeleted ?? 0,
            outcome?.StoresChanged.ToList() ?? []);
        Console.WriteLine(JsonSerializer.Serialize(payload, CatalogueJsonContext.Default.CatalogueJson));
        return 0;
    }
}

/// <summary>The <c>--json</c> shape of a catalogue survey. Stable: the evidence pipeline quotes it.</summary>
public sealed record CatalogueJson(
    string Root,
    int Stores,
    int RunRows,
    int DistinctRuns,
    IReadOnlyList<DuplicateJson> Duplicates,
    IReadOnlyList<string> Deferred,
    bool Applied,
    string? BackupDir,
    int RowsDeleted,
    IReadOnlyList<string> StoresChanged);

/// <summary>One run that lives in more than one store.</summary>
public sealed record DuplicateJson(
    string RunId,
    string Plan,
    string OwnerDb,
    string OwnerReason,
    IReadOnlyList<string> RemoveFrom);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogueJson))]
internal sealed partial class CatalogueJsonContext : JsonSerializerContext;
