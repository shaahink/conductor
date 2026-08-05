using System.ComponentModel;
using System.Globalization;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Money;
using Conductor.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// K4.3 — <c>conductor money</c>. The research doc's headline table (sessions, tokens, cache-read
/// share, cost, checkpoints, tokens per checkpoint, dollars per checkpoint) was produced by
/// hand-written SQL against a database the operator had to locate first, and it is the report the
/// owner keeps asking for. This is that table, computed by <see cref="MoneyAnalyzer"/>, plus the three
/// cuts a lifetime average hides: the windows either side of a ceiling change, the per-stage split,
/// and the calendar month.
/// <para>Every figure is billed dollars and recorded tokens — the engine has no price table by design
/// (<c>LiveCostEstimator</c>), so nothing here is modelled. It reads through <see cref="RunArchive"/>,
/// which opens SQLite <c>Mode=ReadOnly</c>, so pointing it at a live run cannot disturb it.</para>
/// </summary>
public sealed class MoneyCommand : Command<MoneyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[run]")]
        [Description("A run id or its prefix, a catalogue slug, a repo name, or a path to a run.db. Omit for this repo.")]
        public string? Selector { get; init; }

        [CommandOption("--run <ID>")]
        [Description("The run to price. Same as the positional argument.")]
        public string? Run { get; init; }

        [CommandOption("--project|--repo <PATH>")]
        [Description("Price this repo's runs instead of the current directory's. 'all' for every repo on this machine.")]
        public string? Project { get; init; }

        [CommandOption("-p|--plan <NAME>")]
        [Description("Only runs of this plan.")]
        public string? Plan { get; init; }

        [CommandOption("-s|--since <WHEN>")]
        [Description("Only runs active since then: 7d, 2w, 3mo, 1y, or a date.")]
        public string? Since { get; init; }

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
        var selector = string.IsNullOrWhiteSpace(settings.Run) ? settings.Selector : settings.Run;

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

        var project = settings.Project;
        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(project))
            project = Directory.GetCurrentDirectory();
        else if (string.Equals(project, "all", StringComparison.OrdinalIgnoreCase))
            project = null;

        var filter = new RunHistoryFilter(project, settings.Plan, since);
        var sources = RunSources.Resolve(root, filter, selector, project);
        if (sources is null) return 1;

        if (sources.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]no runs to price[/] for [grey]{Markup.Escape(project ?? "any repo")}[/]. " +
                "[grey]try[/] conductor money --project all [grey]or point it at a database:[/] conductor money path/to/run.db");
            return 0;
        }

        var runs = new List<MoneyRun>();
        foreach (var (dbPath, run, label) in sources)
        {
            var archive = RunArchive.TryOpen(dbPath);
            if (archive is null) continue;
            var sessions = archive.Sessions(run.RunId);
            var costs = archive.Costs(run.RunId);
            if (sessions.Count == 0 && costs.Count == 0) continue;
            // The window axis comes from `budget`, unchanged: "what did the cap buy" is the comparison
            // either side of a ceiling change, and there must be one answer to where that change was.
            var windows = BudgetAnalyzer
                .Analyze(run.RunId, run.PlanName, sessions, archive.SoftBreaks(run.RunId)).Windows;
            runs.Add(MoneyAnalyzer.AnalyzeRun(run.RunId, run.PlanName, label,
                run.StartedUtc, run.LastActivityUtc, sessions, costs, windows));
        }

        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]those runs recorded no spend[/] — nothing to price yet.");
            return 0;
        }

        var scope = Scope(selector, settings.Project, settings.Plan, settings.Since);
        var report = MoneyAnalyzer.Combine(scope, runs);

        if (settings.Json)
        {
            Console.WriteLine(MoneyJson.Serialize(report));
            return 0;
        }

        Render(report, detail: !string.IsNullOrWhiteSpace(selector) || runs.Count <= 3);
        return 0;
    }

    private static string Scope(string? selector, string? project, string? plan, string? since)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(selector)) parts.Add(selector);
        else parts.Add(string.IsNullOrWhiteSpace(project) ? Directory.GetCurrentDirectory()
            : string.Equals(project, "all", StringComparison.OrdinalIgnoreCase) ? "every repo" : project);
        if (!string.IsNullOrWhiteSpace(plan)) parts.Add($"plan {plan}");
        if (!string.IsNullOrWhiteSpace(since)) parts.Add($"since {since}");
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ rendering

    private static void Render(MoneyReport report, bool detail)
    {
        AnsiConsole.MarkupLine($"[bold]what it cost[/] [grey]· {Markup.Escape(report.Scope)}[/]");
        AnsiConsole.WriteLine();

        Header("RUN");
        // Identifier first: the label column is fixed-width, and a truncated plan title that has eaten
        // the run id is a row you cannot ask a follow-up question about.
        foreach (var r in report.Runs)
            Row($"{Short(r.RunId)} {r.PlanName}", r.Total);
        if (report.Runs.Count > 1)
        {
            AnsiConsole.MarkupLine("[grey]" + Markup.Escape(new string('-', 84)) + "[/]");
            Row("TOTAL", report.Total, bold: true);
        }
        AnsiConsole.MarkupLine(
            $"[grey]blended[/] [bold]{Money(report.Total.CostPerMillionTokens ?? 0)}[/][grey]/M tokens · " +
            $"{Tokens(report.Total.CacheReadTokens)} of {Tokens(report.Total.Tokens)} tokens were cache reads " +
            $"({Share(report.Total.CacheReadShare)}).[/]");

        if (report.Months.Count > 1 || (report.Months.Count == 1 && report.Runs.Count > 1))
        {
            AnsiConsole.WriteLine();
            Header("MONTH");
            foreach (var m in report.Months) Row(m.Label, m);
        }

        if (report.Categories.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]where the money goes:[/] " + string.Join(" [grey]·[/] ", report.Categories.Select(c =>
                $"[bold]{Markup.Escape(c.Label)}[/] {Money(c.Cost)} " +
                $"[grey]({Percent(report.Total.Cost == 0 ? 0 : (double)(c.Cost / report.Total.Cost))}, {Tokens(c.Tokens)})[/]")));
        }

        if (!detail)
        {
            AnsiConsole.MarkupLine("[grey]name a run for its windows, stages and months:[/] conductor money <run-id>");
            return;
        }

        foreach (var r in report.Runs) Detail(r);
    }

    private static void Detail(MoneyRun run)
    {
        if (run.Windows.Count > 1)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]what the cap bought[/] [grey]· {Markup.Escape(run.PlanName)} {Short(run.RunId)}[/]");
            Header("WINDOW");
            foreach (var w in run.Windows) Row(w.Label, w);
            if (run.CapCostPayoff is { } payoff)
            {
                var better = payoff >= 1;
                AnsiConsole.MarkupLine(
                    $"[grey]the change bought[/] [bold]{payoff.ToString("0.0", CultureInfo.InvariantCulture)}x[/] " +
                    (better ? "[green]better[/]" : "[red]WORSE[/]") + " [grey]dollars per delivered checkpoint" +
                    (run.CapTokenPayoff is { } t ? $" ({t.ToString("0.0", CultureInfo.InvariantCulture)}x on tokens)" : "") + ".[/]");
            }
        }

        if (run.Stages.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]by stage[/] [grey]· {Markup.Escape(run.PlanName)} {Short(run.RunId)}[/]");
            Header("STAGE");
            foreach (var s in run.Stages) Row(s.Label, s);
        }

        if (run.Months.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]by month[/] [grey]· {Markup.Escape(run.PlanName)} {Short(run.RunId)}[/]");
            Header("MONTH");
            foreach (var m in run.Months) Row(m.Label, m);
        }
    }

    private static void Header(string first) =>
        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.Join(' ', Cells(
            first, "SESS", "TOKENS", "CACHE", "COST", "CKPT", "TOK/CKPT", "$/CKPT"))) + "[/]");

    private static void Row(string label, MoneyLine l, bool bold = false)
    {
        var cells = Cells(label,
            l.Sessions.ToString(CultureInfo.InvariantCulture),
            Tokens(l.Tokens),
            Share(l.CacheReadShare),
            Money(l.Cost),
            l.Checkpoints > 0 ? l.Checkpoints.ToString(CultureInfo.InvariantCulture) : "-",
            l.TokensPerCheckpoint is { } t ? Tokens((long)t) : "-",
            l.CostPerCheckpoint is { } c ? Money(c) : "-");
        var text = Markup.Escape(string.Join(' ', cells));
        AnsiConsole.MarkupLine(bold ? $"[bold]{text}[/]" : text);
    }

    private static string Short(string runId) => runId.Length > 8 ? runId[..8] : runId;

    private static string Tokens(long tokens) => BudgetAnalyzer.Millions(tokens);

    private static string Money(decimal usd) => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The cache-read share, always to one decimal: 98.5% and 98.2% are different findings
    /// about the same run, and rounding both to "98%" throws away the only precision this column has.</summary>
    private static string Share(double share) =>
        share <= 0 ? "-" : (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Percent(double share) =>
        (share * 100).ToString(share is > 0 and < 0.005 ? "0.0#" : "0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Fixed columns padded as PLAIN text, coloured only afterwards — the rule
    /// <c>HistoryCommand</c> and <c>BudgetCommand</c> both follow, and for the same reason: padding a
    /// string that already carries escape bytes pads the escapes.</summary>
    private static string[] Cells(params string[] values)
    {
        int[] widths = [34, 5, 8, 6, 10, 5, 9, 8];
        var result = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var w = i < widths.Length ? widths[i] : 8;
            var v = values[i].Length > w ? values[i][..w] : values[i];
            result[i] = i == 0 ? v.PadRight(w) : v.PadLeft(w);
        }
        return result;
    }
}
