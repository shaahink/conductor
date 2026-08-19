using System.ComponentModel;
using System.Globalization;

using Conductor.Core.History;
using Conductor.Core.Interop;
using Conductor.Core.Store;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS8.2 — <c>conductor history export &lt;run&gt; --atif</c>: a finished run as an ATIF trajectory,
/// the Harbor / Terminal-Bench interchange format. Read-only, like everything under <c>history</c>.
/// </summary>
/// <remarks>
/// Registered as the hidden verb <c>history-export</c> and reached by the argv rewrite in
/// <c>Program.cs</c>, for the reason <c>run close|adopt</c> is: Spectre cannot have <c>history</c> be
/// both a branch holding subcommands and the verb that lists the catalogue, and listing the
/// catalogue is what <c>history</c> is for. <c>docs/cli.md</c> documents it under <c>history</c>.
/// </remarks>
public sealed class HistoryExportCommand : AsyncCommand<HistoryExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[run]")]
        [Description("A run id or its prefix, a catalogue slug, or a repo name. Omit only with --all.")]
        public string? Run { get; init; }

        [CommandOption("--atif")]
        [Description("Write ATIF (Agent Trajectory Interchange Format). Currently the only format.")]
        public bool Atif { get; init; }

        [CommandOption("-o|--output <PATH>")]
        [Description("Write here instead of stdout. With --all this must be a directory.")]
        public string? Output { get; init; }

        [CommandOption("--all")]
        [Description("Export every readable run in the catalogue. Requires -o <DIR>.")]
        public bool All { get; init; }

        [CommandOption("--home <PATH>")]
        [Description("Read a state home other than this machine's.")]
        public string? Home { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Atif)
        {
            AnsiConsole.MarkupLine("[red]name a format[/] — the only one is [bold]--atif[/]. "
                + "[grey]conductor history export <run> --atif[/]");
            return 2;
        }

        var root = string.IsNullOrWhiteSpace(settings.Home) ? StateHome.Root : Path.GetFullPath(settings.Home);
        var now = DateTimeOffset.UtcNow;
        return settings.All
            ? await ExportAll(root, settings, now).ConfigureAwait(false)
            : await ExportOne(root, settings, now).ConfigureAwait(false);
    }

    private static async Task<int> ExportOne(string root, Settings settings, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(settings.Run))
        {
            AnsiConsole.MarkupLine("[red]name a run[/] — or pass [bold]--all -o <DIR>[/] for every one. "
                + "[grey]conductor history[/] lists them.");
            return 2;
        }

        var view = ArchiveView.Open(root, settings.Run, out var refusal);
        if (view is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(refusal)}[/]");
            return 1;
        }

        var json = Trajectory(view, now);
        if (json is null) return 1;

        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            // Straight to stdout so `... --atif > karvan.json` works, and so nothing is written to a
            // path the operator did not name.
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
            return 0;
        }

        var path = Path.GetFullPath(settings.Output);
        await WriteFile(path, json).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]wrote[/] {Markup.Escape(path)} "
            + $"[grey]({Bytes(json.Length)}, {view.Run.PlanName})[/]");
        return 0;
    }

    private static async Task<int> ExportAll(string root, Settings settings, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            AnsiConsole.MarkupLine("[red]--all needs a directory[/] — [grey]-o <DIR>[/]. "
                + "Thirty trajectories do not belong on stdout.");
            return 2;
        }

        var dir = Path.GetFullPath(settings.Output);
        Directory.CreateDirectory(dir);

        var written = 0;
        var skipped = 0;
        foreach (var row in RunHistory.List(root))
        {
            if (row.Run is null) { skipped++; continue; }
            var view = ArchiveView.OpenDb(row.RunDbPath, row.Run.RunId, out var refusal);
            if (view is null)
            {
                AnsiConsole.MarkupLine($"[yellow]skipped[/] {Markup.Escape(row.Slug)} [grey]{Markup.Escape(refusal)}[/]");
                skipped++;
                continue;
            }
            var json = Trajectory(view, now);
            if (json is null) { skipped++; continue; }
            var path = Path.Combine(dir, $"{row.Run.ShortRunId}.atif.json");
            await WriteFile(path, json).ConfigureAwait(false);
            written++;
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(row.Run.ShortRunId)}[/] "
                + $"{Markup.Escape(Truncate(row.Run.PlanName, 44))} [grey]{Bytes(json.Length)}[/]");
        }

        AnsiConsole.MarkupLine($"\n[bold]{written.ToString(CultureInfo.InvariantCulture)}[/] trajectories in "
            + $"{Markup.Escape(dir)}"
            + (skipped > 0 ? $" [grey]({skipped.ToString(CultureInfo.InvariantCulture)} skipped — no readable run row)[/]" : ""));
        return written > 0 ? 0 : 1;
    }

    /// <summary>Build one trajectory, or null with a printed refusal.</summary>
    private static string? Trajectory(ArchiveView view, DateTimeOffset now)
    {
        var archive = RunArchive.TryOpen(view.RunDbPath, out var problem);
        if (archive is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ArchiveView.Describe(view.RunDbPath, problem))}[/]");
            return null;
        }
        var run = view.Run;
        return AtifExport.Serialize(run, RunHistory.RepoLabel(view.Repo), view.Status,
            archive.Sessions(run.RunId), archive.Costs(run.RunId), view.Log(), now);
    }

    private static async Task WriteFile(string path, string json)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }

    private static string Bytes(int count) => count switch
    {
        < 1024 => $"{count.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(count / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        _ => $"{(count / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
