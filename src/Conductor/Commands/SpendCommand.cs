using System.ComponentModel;
using Conductor.Core.History;
using Conductor.Core.Money;
using Conductor.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS5.1 — <c>conductor spend</c>. "What did this machine spend this week, and this month." Until now
/// that question had no answer: <c>money</c> prices a run or a repo and wants to be told which, and
/// this machine's history is spread over nineteen catalogue entries the operator does not hold in
/// their head. So the verb takes no repo and no plan — it reads the whole state home.
/// <para><b>The window is a session's start, not a run's.</b> <c>--since</c> everywhere else in this
/// CLI filters WHOLE runs by last activity, which would put a June session's bill inside "this week"
/// for any run that touched today. <see cref="MachineLedger"/> slices at session granularity through
/// the only timestamp the <c>costs</c> table can be joined to.</para>
/// <para><b>Every dollar is a billed row.</b> No price table, here or anywhere: the figures are
/// <c>costs.cost_usd</c> summed by the same function <c>money</c> sums with, which is what makes the
/// two verbs cross-check to the cent.</para>
/// </summary>
public sealed class SpendCommand : Command<SpendCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--since <WHEN>")]
        [Description("One window instead of the default ladder: 7d, 2w, 1mo, 1y, or a date.")]
        public string? Since { get; init; }

        [CommandOption("--runs")]
        [Description("Also list every run counted, oldest first.")]
        public bool Runs { get; init; }

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
        var now = DateTimeOffset.UtcNow;

        IReadOnlyList<MachineLedgerWindow> windows;
        if (string.IsNullOrWhiteSpace(settings.Since))
        {
            windows = MachineLedger.Ladder(now);
        }
        else
        {
            var since = RunHistory.ParseSince(settings.Since, now);
            if (since is null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]--since '{Markup.Escape(settings.Since)}' means nothing.[/] try 7d, 2w, 3mo, 1y, or a date.");
                return 2;
            }
            windows = [new MachineLedgerWindow($"since {settings.Since.Trim()}", since.Value, null)];
        }

        var report = Measure(root, windows, Scope(settings.Since));

        if (settings.Json)
        {
            Console.WriteLine(MoneyJson.SerializeLedger(report));
            return 0;
        }

        Render(report, settings.Runs);
        return 0;
    }

    /// <summary>
    /// Resolve every store, read each run out of it, measure, roll up. Internal so the tests drive
    /// the path the verb drives instead of a second copy of it standing beside the first.
    /// <para><see cref="RunSources"/> is the ONE run resolver this codebase has and it stays that way:
    /// no selector and no repo means "every catalogued store, and failing that this directory's own
    /// run.db". Null only ever comes back from the selector branch, which there is none of here.</para>
    /// <para>The filter is deliberately <see cref="RunHistoryFilter.All"/>. Passing the window into it
    /// would drop whole runs by last activity before the session-level slice ever ran — which is the
    /// exact bug this verb exists to not have.</para>
    /// </summary>
    internal static MachineLedgerReport Measure(
        string root, IReadOnlyList<MachineLedgerWindow> windows, string scope)
    {
        var sources = RunSources.Resolve(root, RunHistoryFilter.All, selector: null, repo: null) ?? [];
        var measured = new List<MachineLedgerRun>();
        foreach (var (dbPath, run, label) in sources)
        {
            var archive = RunArchive.TryOpen(dbPath);
            if (archive is null) continue;
            var sessions = archive.Sessions(run.RunId);
            var costs = archive.Costs(run.RunId);
            if (sessions.Count == 0 && costs.Count == 0) continue;
            measured.Add(MachineLedger.Measure(dbPath, run.RunId, run.PlanName, label,
                run.StartedUtc, run.LastActivityUtc, sessions, costs, windows));
        }
        return MachineLedger.Build(scope, root, windows, measured);
    }

    private static string Scope(string? since) =>
        string.IsNullOrWhiteSpace(since) ? "this machine" : $"this machine · since {since.Trim()}";

    // ------------------------------------------------------------------ rendering

    private static void Render(MachineLedgerReport report, bool listRuns)
    {
        AnsiConsole.MarkupLine($"[bold]what this machine spent[/] [grey]· {Markup.Escape(report.Root)}[/]");
        AnsiConsole.WriteLine();

        // Nothing recorded is an ANSWER, not a failure: a machine with no catalogue and no local
        // database has spent nothing it kept, and exiting non-zero over that would make every script
        // that asks the question treat a fresh machine as a broken one.
        if (report.NothingRecorded)
        {
            AnsiConsole.MarkupLine(
                "[yellow]nothing recorded[/] [grey]— no catalogued run store and no .conductor/run.db here. " +
                "This machine has kept no spend to report.[/]");
            return;
        }

        MoneyCommand.Header("PERIOD");
        foreach (var p in report.Periods) MoneyCommand.Row(p.Label, p);
        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(new string('-', 84)) + "[/]");
        MoneyCommand.Row(report.Total.Label, report.Total, bold: true);

        AnsiConsole.MarkupLine(
            $"[grey]{report.Runs.Count} run(s) across {report.Stores} store(s)" +
            (report.DuplicateRunsCollapsed > 0
                ? $" · {report.DuplicateRunsCollapsed} duplicate catalogue row(s) collapsed — each run counted once"
                : "") + ".[/]");

        // Said out loud rather than folded in silently: it is in the lifetime total (so this verb and
        // `money` agree) and in no period (so no week's figure claims money nobody can date).
        if (report.Undated.Cost > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]{MoneyCommand.Money(report.Undated.Cost)}[/] [grey]of that is billed against sessions " +
                "with no start time — counted in the lifetime total, in none of the periods above.[/]");

        if (report.Ledger.Categories.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]where the money goes:[/] " + string.Join(" [grey]·[/] ",
                report.Ledger.Categories.Select(c =>
                    $"[bold]{Markup.Escape(c.Label)}[/] {MoneyCommand.Money(c.Cost)} " +
                    $"[grey]({MoneyCommand.Percent(report.Total.Cost == 0 ? 0 : (double)(c.Cost / report.Total.Cost))}, " +
                    $"{MoneyCommand.Tokens(c.Tokens)})[/]")));
        }

        if (report.Ledger.Months.Count > 1)
        {
            AnsiConsole.WriteLine();
            MoneyCommand.Header("MONTH");
            foreach (var m in report.Ledger.Months) MoneyCommand.Row(m.Label, m);
        }

        if (!listRuns)
        {
            AnsiConsole.MarkupLine("[grey]every run behind these numbers:[/] conductor spend --runs");
            return;
        }

        AnsiConsole.WriteLine();
        MoneyCommand.Header("RUN");
        foreach (var r in report.Runs)
            MoneyCommand.Row($"{MoneyCommand.Short(r.Run.RunId)} {r.Run.RepoLabel} {r.Run.PlanName}", r.Run.Total);
        AnsiConsole.MarkupLine("[grey]price one of them:[/] conductor money <run-id>");
    }
}
