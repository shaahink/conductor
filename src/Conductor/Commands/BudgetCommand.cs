using System.ComponentModel;
using System.Globalization;
using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// K4.2 — <c>conductor budget</c>. The method for choosing a token ceiling has been written down since
/// <c>docs/dev/TOKEN-BUDGET-TUNING.md</c> and implemented nowhere: measure the floor, measure the
/// wrap-up, put the cap above one and the nudge above the other. Every figure in that document was
/// produced by a hand-written query against a database the operator had to find first.
/// <para>This verb does the measurement. It takes no numbers from its caller — not the cap, not the
/// ratio, not the floor — because the run recorded all of them: the nudge stamped its own ceiling on
/// every <c>SoftBreakRequested</c> event, the kills cluster on it, and <c>newly_done</c> says which
/// sessions actually delivered. It reads through <see cref="RunArchive"/>, which is read-only, so
/// pointing it at a live run cannot disturb it.</para>
/// <para>With no argument it profiles every run of the repo you are standing in, which is what makes
/// "what did the cap buy me" answerable: the windows either side of a cap change are the comparison.</para>
/// </summary>
public sealed class BudgetCommand : Command<BudgetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[run]")]
        [Description("A run id or its prefix, a catalogue slug, or a repo name. Omit for this repo's runs.")]
        public string? Run { get; init; }

        [CommandOption("-r|--repo <PATH>")]
        [Description("Profile this repo's runs instead of the current directory's. 'all' for every repo.")]
        public string? Repo { get; init; }

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

        var repo = settings.Repo;
        if (string.IsNullOrWhiteSpace(settings.Run) && string.IsNullOrWhiteSpace(repo))
            repo = Directory.GetCurrentDirectory();
        else if (string.Equals(repo, "all", StringComparison.OrdinalIgnoreCase))
            repo = null;

        var filter = new RunHistoryFilter(repo, settings.Plan, since);
        // K4.3 moved the resolution to RunSources so `money` finds a run the way `budget` does.
        var sources = RunSources.Resolve(root, filter, settings.Run, repo);
        if (sources is null) return 1;

        if (sources.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]no runs to measure[/] for [grey]{Markup.Escape(repo ?? "any repo")}[/]. " +
                "[grey]a budget is measured from a run's own sessions; try[/] conductor budget --repo all " +
                "[grey]or point it at a database:[/] conductor budget path/to/run.db");
            return 0;
        }

        var profiles = new List<(string Label, BudgetProfile Profile)>();
        foreach (var (dbPath, run, label) in sources)
        {
            var archive = RunArchive.TryOpen(dbPath);
            if (archive is null) continue;
            var sessions = archive.Sessions(run.RunId);
            if (sessions.Count == 0) continue;
            profiles.Add((label, BudgetAnalyzer.Analyze(
                run.RunId, run.PlanName, sessions, archive.SoftBreaks(run.RunId))));
        }

        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]those runs recorded no sessions[/] — nothing to measure yet.");
            return 0;
        }

        if (settings.Json)
        {
            Console.WriteLine(BudgetJson.Serialize(profiles.Select(p => p.Profile).ToList()));
            return 0;
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            if (i > 0) AnsiConsole.WriteLine();
            Render(profiles[i].Label, profiles[i].Profile);
        }
        AnsiConsole.MarkupLine("[grey]method:[/] docs/dev/TOKEN-BUDGET-TUNING.md section 7 · [grey]every number above is measured from the run's own ledger.[/]");
        return 0;
    }

    // ------------------------------------------------------------------ rendering

    private static void Render(string repoLabel, BudgetProfile profile)
    {
        AnsiConsole.MarkupLine(
            $"[bold aqua]{Markup.Escape(profile.RunId[..8])}[/] [bold]{Markup.Escape(profile.PlanName)}[/] " +
            $"[grey]· {Markup.Escape(repoLabel)}[/]");

        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.Join(' ', Cells(
            "WINDOW", "SESS", "TOKENS", "CKPT", "TOK/CKPT", "FLOOR", "MED CLOSER", "ROLLOVER", "WRAP-UP"))) + "[/]");
        foreach (var w in profile.Windows)
            AnsiConsole.MarkupLine(Markup.Escape(string.Join(' ', Cells(
                $"{w.FirstSession}-{w.LastSession} {w.Label}",
                w.Costed.ToString(CultureInfo.InvariantCulture),
                M(w.Tokens),
                w.Checkpoints.ToString(CultureInfo.InvariantCulture),
                w.TokensPerCheckpoint is { } t ? M((long)t) : "-",
                w.Closers > 0 ? M(w.Floor) : "-",
                w.Closers > 0 ? M(w.ClosingMedian) : "-",
                w.Rollovers == 0 ? "0" : $"{w.Rollovers}/{w.Costed} {(w.RolloverRate * 100).ToString("0", CultureInfo.InvariantCulture)}%",
                w.WrapUp is { } u ? $"{M(u.Median)} (n={u.Samples})" : "-"))));

        if (profile.CapPayoff is { } payoff)
        {
            var better = payoff >= 1;
            AnsiConsole.MarkupLine(
                $"[grey]what the change bought:[/] [bold]{payoff.ToString("0.0", CultureInfo.InvariantCulture)}x[/] " +
                (better ? "[green]better[/]" : "[red]WORSE[/]") + " [grey]tokens per delivered checkpoint.[/]");
        }

        var c = profile.Current;
        AnsiConsole.MarkupLine(
            "[grey]now:[/] " + (c.CapTokens is { } cap
                ? $"cap [bold]{M(cap)}[/]{(c.CapMeasured ? "" : " [grey](inferred from where the kills cluster)[/]")}" +
                  (c.NudgeTokens is { } n
                      ? $" · nudge [bold]{M(n)}[/] ([grey]{(c.NudgeRatio ?? 0).ToString("0.00", CultureInfo.InvariantCulture)}[/]) · " +
                        $"nudge vs floor [bold]{(n / (double)Math.Max(c.Floor, 1)).ToString("0.00", CultureInfo.InvariantCulture)}x[/] · " +
                        $"vs median closer [bold]{(n / (double)Math.Max(c.ClosingMedian, 1)).ToString("0.00", CultureInfo.InvariantCulture)}x[/] · " +
                        $"headroom {(c.Headroom is { } h ? M(h) : "-")}"
                      : " · [yellow]the rail never fired in this window[/]")
                : "[yellow]uncapped[/]"));

        foreach (var f in profile.Prescription.Findings)
            AnsiConsole.MarkupLine("  [yellow]![/] " + Markup.Escape(f));
        AnsiConsole.MarkupLine("  [bold green]>[/] " + Markup.Escape(profile.Prescription.Verdict));
        foreach (var line in profile.Prescription.AsJsonc.Split('\n'))
            AnsiConsole.MarkupLine("    [grey]" + Markup.Escape(line) + "[/]");
    }

    private static string M(long tokens) => BudgetAnalyzer.Millions(tokens);

    /// <summary>Fixed columns padded as PLAIN text, coloured only afterwards — the same rule
    /// <c>HistoryCommand</c> follows, and for the same reason: padding a string that already carries
    /// escape bytes pads the escapes.</summary>
    private static string[] Cells(params string[] values)
    {
        int[] widths = [34, 4, 8, 4, 8, 7, 10, 10, 14];
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
