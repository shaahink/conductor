using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Conductor.Core.History;
using Conductor.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// K3.2 — <c>conductor history</c>. Before K3.1 there was nothing to browse: the store lived under
/// <c>.conductor/</c>, which is git-ignored with a bare <c>*</c>, so every run died with its working
/// tree. The catalogue now knows every run store on this machine, and this verb reads it.
/// <para>With no argument it lists. With one it opens that run and replays its spine — stages,
/// checkpoints, sessions. Both go through <see cref="RunArchive"/>, which is <c>Mode=ReadOnly</c>:
/// this verb cannot alter a finished run, and no code path here tries.</para>
/// <para>It takes no plan and needs no repo. History is a property of the machine, so running it in
/// a directory that has never held a plan still answers.</para>
/// </summary>
public sealed class HistoryCommand : Command<HistoryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[run]")]
        [Description("A run id or its prefix, a catalogue slug, or a repo name. Omit to list.")]
        public string? Run { get; init; }

        [CommandOption("-r|--repo <PATH>")]
        [Description("Only runs of this repo. A full path or just its directory name.")]
        public string? Repo { get; init; }

        [CommandOption("-p|--plan <NAME>")]
        [Description("Only runs of this plan.")]
        public string? Plan { get; init; }

        [CommandOption("-s|--since <WHEN>")]
        [Description("Only runs active since then: 7d, 2w, 3mo, 1y, or a date.")]
        public string? Since { get; init; }

        [CommandOption("-n|--limit <COUNT>")]
        [Description("How many runs to list. Default 20; 0 lists all.")]
        public int Limit { get; init; } = 20;

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

        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            since = RunHistory.ParseSince(settings.Since, DateTimeOffset.UtcNow);
            if (since is null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]--since '{Markup.Escape(settings.Since)}' means nothing.[/] try 7d, 2w, 3mo, 1y, or a date.");
                return 2;
            }
        }

        var filter = new RunHistoryFilter(settings.Repo, settings.Plan, since);
        return string.IsNullOrWhiteSpace(settings.Run)
            ? ShowList(root, filter, settings)
            : ShowOne(root, filter, settings);
    }

    // ------------------------------------------------------------------ the listing

    private static int ShowList(string root, RunHistoryFilter filter, Settings settings)
    {
        var rows = RunHistory.List(root, filter);
        var shown = settings.Limit > 0 ? rows.Take(settings.Limit).ToList() : rows.ToList();

        if (settings.Json)
        {
            var payload = new RunHistoryListJson(shown.Select(ToJson).ToList());
            Console.WriteLine(JsonSerializer.Serialize(payload, RunHistoryJsonContext.Default.RunHistoryListJson));
            return 0;
        }

        if (rows.Count == 0)
        {
            var filtered = filter.Repo is not null || filter.Plan is not null || filter.Since is not null;
            AnsiConsole.MarkupLine(filtered
                ? $"[yellow]no runs match[/] in [grey]{Markup.Escape(root)}[/]. drop a filter to see the rest."
                : $"[yellow]no runs in the catalogue[/] under [grey]{Markup.Escape(root)}[/]. " +
                  "[grey]a run is catalogued the first time its plan resolves a state home.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[bold aqua]Conductor[/] — history · [bold]{rows.Count}[/] {(rows.Count == 1 ? "run" : "runs")} · [grey]{Markup.Escape(root)}[/]");
        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.Join(' ', Cells(
            "RUN", "REPO", "PLAN", "STATUS", "CKPT", "SESS", "COST", "LAST"))) + "[/]");

        // Fixed columns, laid out as PLAIN text and coloured only once the padding is done. Padding a
        // string that already carries escape bytes pads the escapes — the same rule face-go's STYLE.md
        // states, and the reason a bordered Spectre table was wrong here: this repo's plan names are
        // sentences, and inside one three runs became eighteen wrapped lines with the run id itself
        // broken across two of them.
        foreach (var r in shown)
        {
            if (!r.Readable)
            {
                var gone = Cells("-", r.RepoLabel, r.Plan, "gone", "-", "-", "-", "db missing");
                AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.Join(' ', gone[..3])) + "[/] "
                    + "[red]" + Markup.Escape(gone[3]) + "[/] "
                    + "[grey]" + Markup.Escape(string.Join(' ', gone[4..])) + "[/]");
                continue;
            }
            var run = r.Run!;
            var (done, total) = RunHistory.CheckpointCounts(r);
            var c = Cells(
                run.ShortRunId,
                r.RepoLabel,
                string.IsNullOrEmpty(run.PlanName) ? r.Plan : run.PlanName,
                run.Status,
                total == 0 ? "-" : $"{done}/{total}",
                run.Sessions.ToString(CultureInfo.InvariantCulture),
                "$" + run.CostUsd.ToString("0.00", CultureInfo.InvariantCulture),
                Ago(run.LastActivityUtc));
            AnsiConsole.MarkupLine(string.Join(' ',
                $"[aqua]{Markup.Escape(c[0])}[/]",
                Markup.Escape(c[1]),
                Markup.Escape(c[2]),
                Colour(c[3], run.Status),
                Markup.Escape(c[4]),
                Markup.Escape(c[5]),
                Markup.Escape(c[6]),
                $"[grey]{Markup.Escape(c[7])}[/]"));
        }

        if (settings.Limit > 0 && rows.Count > shown.Count)
            AnsiConsole.MarkupLine($"[grey]{rows.Count - shown.Count} older runs not shown — raise --limit or pass 0.[/]");
        AnsiConsole.MarkupLine("[grey]open one:[/] conductor history <run-id|repo|slug>");
        return 0;
    }

    // ------------------------------------------------------------------ one run, read-only

    private static int ShowOne(string root, RunHistoryFilter filter, Settings settings)
    {
        var row = RunHistory.Find(root, settings.Run!, out var ambiguous, filter);
        if (row is null)
        {
            if (ambiguous.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]no run matches '{Markup.Escape(settings.Run!)}'.[/] try [grey]conductor history[/].");
                return 1;
            }
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(settings.Run!)}' matches {ambiguous.Count} runs:[/]");
            foreach (var c in ambiguous)
                AnsiConsole.MarkupLine(
                    $"  [aqua]{Markup.Escape(c.Run!.RunId)}[/]  {Markup.Escape(c.RepoLabel)}  [grey]{Markup.Escape(c.Plan)}[/]");
            return 1;
        }

        var run = row.Run!;
        var archive = RunArchive.TryOpen(row.RunDbPath);
        if (archive is null)
        {
            AnsiConsole.MarkupLine($"[red]cannot read[/] {Markup.Escape(row.RunDbPath)}");
            return 1;
        }

        var stages = archive.Stages(run.RunId);
        var checkpoints = archive.Checkpoints(run.RunId);
        var sessions = archive.Sessions(run.RunId);

        if (settings.Json)
        {
            var payload = new RunHistoryDetailJson(ToJson(row), stages, checkpoints, sessions);
            Console.WriteLine(JsonSerializer.Serialize(payload, RunHistoryJsonContext.Default.RunHistoryDetailJson));
            return 0;
        }

        var done = checkpoints.Count(c => string.Equals(c.Status, "DONE", StringComparison.Ordinal));
        AnsiConsole.MarkupLine(
            $"[bold aqua]{Markup.Escape(run.RunId)}[/] · [bold]{Markup.Escape(run.PlanName)}[/] · {StatusMarkup(run.Status)} [grey](read-only)[/]");
        AnsiConsole.MarkupLine($"[grey]repo[/]     {Markup.Escape(run.Repo)}");
        // K3.3: the stamp, and it says when it cannot answer. A run older than schema v11 carries the
        // assembly version and nothing else, and "unrecorded" beside it is the honest label — the
        // alternative is a reader trusting 2.0.0.0 as if it identified a build.
        var dirty = run.EngineDirty == true ? " [red](dirty build)[/]" : "";
        var provenance = run.EngineCommit is null ? "  [grey](commit unrecorded)[/]" : "";
        AnsiConsole.MarkupLine(
            $"[grey]engine[/]   {Markup.Escape(run.EngineStampText ?? "unrecorded")}{dirty}{provenance}  " +
            $"[grey]branch[/] {Markup.Escape(run.Branch ?? "-")}");
        if (run.Limits is { } limits)
            AnsiConsole.MarkupLine($"[grey]limits[/]   {Markup.Escape(limits.Describe())}");
        AnsiConsole.MarkupLine(
            $"[grey]ran[/]      {Markup.Escape(Stamp(run.StartedUtc) ?? "-")} → {Markup.Escape(Stamp(run.EndedUtc) ?? "still open")}");
        AnsiConsole.MarkupLine(
            $"[grey]totals[/]   {done}/{checkpoints.Count} checkpoints · {sessions.Count} sessions · " +
            $"${run.CostUsd.ToString("0.00", CultureInfo.InvariantCulture)} · {run.Tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens");
        AnsiConsole.MarkupLine($"[grey]db[/]       {Markup.Escape(row.RunDbPath)}");
        if (!string.IsNullOrEmpty(row.ImportedFrom))
            AnsiConsole.MarkupLine($"[grey]imported[/] {Markup.Escape(row.ImportedFrom)}");
        AnsiConsole.WriteLine();

        if (stages.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).Title("[bold]stages[/]")
                .AddColumn("Stage").AddColumn("Title").AddColumn("Status")
                .AddColumn(new TableColumn("Sessions").RightAligned());
            foreach (var s in stages)
                t.AddRow(Markup.Escape(s.Id), Markup.Escape(Clip(s.Title, 46)), StatusMarkup(s.Status),
                    s.Sessions.ToString(CultureInfo.InvariantCulture));
            AnsiConsole.Write(t);
        }

        if (checkpoints.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).Title("[bold]checkpoints[/]")
                .AddColumn("ID").AddColumn("Status").AddColumn("Title").AddColumn("Evidence");
            foreach (var c in checkpoints)
                t.AddRow(Markup.Escape(c.Id), StatusMarkup(c.Status), Markup.Escape(Clip(c.Title, 38)),
                    $"[grey]{Markup.Escape(Clip(c.Evidence ?? "-", 24))}[/]");
            AnsiConsole.Write(t);
        }

        if (sessions.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).Title("[bold]sessions[/]")
                .AddColumn(new TableColumn("#").RightAligned())
                .AddColumn("Stage").AddColumn("Kind").AddColumn("Outcome")
                .AddColumn(new TableColumn("Commits").RightAligned())
                .AddColumn(new TableColumn("Cost").RightAligned())
                .AddColumn("Started").AddColumn("Result");
            foreach (var s in sessions)
                t.AddRow(
                    s.Number.ToString(CultureInfo.InvariantCulture),
                    Markup.Escape(s.StageId), Markup.Escape(s.Kind),
                    StatusMarkup(s.Outcome ?? "-"),
                    s.Commits.ToString(CultureInfo.InvariantCulture),
                    $"${s.CostUsd.ToString("0.00", CultureInfo.InvariantCulture)}",
                    $"[grey]{Markup.Escape(Stamp(s.StartedUtc) ?? "-")}[/]",
                    Markup.Escape(Clip(s.ResultSummary ?? "-", 30)));
            AnsiConsole.Write(t);
            ShowLimitChanges(sessions);
        }

        return 0;
    }

    /// <summary>
    /// K3.3 — where the limits or the binary changed mid-run, and to what. This is the checkpoint's
    /// whole reason: the Sarban run raised its session cap partway through, and with only a run-level
    /// snapshot the change is invisible, leaving the shape of a token curve as the evidence. Printed
    /// only when something actually changed, so an ordinary run gains no noise.
    /// </summary>
    private static void ShowLimitChanges(IReadOnlyList<Conductor.Core.History.ArchivedSession> sessions)
    {
        string? lastLimits = null, lastEngine = null;
        var lines = new List<string>();
        foreach (var s in sessions)
        {
            var limits = s.Limits?.Describe();
            if (limits is not null && lastLimits is not null && !string.Equals(limits, lastLimits, StringComparison.Ordinal))
                lines.Add($"[yellow]limits changed at session {s.Number}[/] [grey]{Markup.Escape(lastLimits)}[/] → {Markup.Escape(limits)}");
            if (s.Engine is not null && lastEngine is not null && !string.Equals(s.Engine, lastEngine, StringComparison.Ordinal))
                lines.Add($"[yellow]engine changed at session {s.Number}[/] [grey]{Markup.Escape(lastEngine)}[/] → {Markup.Escape(s.Engine)}");
            lastLimits = limits ?? lastLimits;
            lastEngine = s.Engine ?? lastEngine;
        }
        foreach (var line in lines)
            AnsiConsole.MarkupLine(line);
    }

    // ------------------------------------------------------------------ shaping

    private static RunHistoryItemJson ToJson(RunHistoryRow r)
    {
        if (r.Run is null)
            return new RunHistoryItemJson("", r.Repo, r.Plan, "unreadable", null, null, null, null, null,
                0, 0, 0, 0m, 0, r.RunDbPath, r.Slug, r.ImportedFrom, Readable: false);
        var (done, total) = RunHistory.CheckpointCounts(r);
        return new RunHistoryItemJson(
            r.Run.RunId, r.Run.Repo, string.IsNullOrEmpty(r.Run.PlanName) ? r.Plan : r.Run.PlanName,
            r.Run.Status, r.Run.EngineStampText, r.Run.Branch,
            r.Run.StartedUtc, r.Run.EndedUtc, r.Run.LastActivityUtc,
            r.Run.Sessions, done, total, r.Run.CostUsd, r.Run.Tokens,
            r.RunDbPath, r.Slug, r.ImportedFrom, Readable: true,
            r.Run.EngineCommit, r.Run.EngineDirty, r.Run.Limits);
    }

    private static string StatusMarkup(string status) => status.ToLowerInvariant() switch
    {
        "completed" or "confirmed" or "done" or "advance" or "advanced" => $"[green]{Markup.Escape(status)}[/]",
        "running" or "in progress" or "in_progress" or "active" => $"[yellow]{Markup.Escape(status)}[/]",
        "failed" or "aborted" or "blocked" or "stuck" => $"[red]{Markup.Escape(status)}[/]",
        _ => $"[grey]{Markup.Escape(status)}[/]",
    };

    private static string? Stamp(string? raw)
        => RunHistory.ParseUtc(raw)?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Relative when it is recent enough for "when" to mean something, absolute after that.</summary>
    private static string Ago(string? raw)
    {
        if (RunHistory.ParseUtc(raw) is not { } when) return "-";
        var d = DateTimeOffset.UtcNow - when;
        if (d < TimeSpan.Zero) return Stamp(raw) ?? "-";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 48) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Clip(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Column widths of the listing. Eight columns, seven single-space gaps, 77 columns
    /// total — it fits an eighty-column terminal with room for a prompt.</summary>
    private static readonly (int Width, bool Right)[] ListLayout =
        [(8, false), (12, false), (15, false), (8, false), (6, true), (4, true), (8, true), (9, false)];

    /// <summary>Clips and pads each cell as PLAIN text. Colour is applied to the result, never
    /// before: padding a string that already carries escape bytes pads the escapes.</summary>
    private static string[] Cells(params string[] cells)
    {
        var parts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var (width, right) = ListLayout[i];
            var text = Clip(cells[i], width);
            parts[i] = right ? text.PadLeft(width) : text.PadRight(width);
        }
        return parts;
    }

    /// <summary>Colours an already-padded cell, keeping its width.</summary>
    private static string Colour(string padded, string status)
    {
        var escaped = Markup.Escape(padded);
        return status.ToLowerInvariant() switch
        {
            "completed" or "confirmed" or "done" => $"[green]{escaped}[/]",
            "running" or "active" => $"[yellow]{escaped}[/]",
            "failed" or "aborted" or "blocked" => $"[red]{escaped}[/]",
            _ => $"[grey]{escaped}[/]",
        };
    }
}
