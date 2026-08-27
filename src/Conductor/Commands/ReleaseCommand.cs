using System.ComponentModel;
using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Release;
using Conductor.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// CH4.1 — closing an era stops being prose.
///
/// <para><b>What this replaces.</b> <c>.conductor/evidence/DV7/dv7-3-owner-runbook.md</c> is the best
/// hand-written era-close this project has produced: seven acts, each measured, each with the exact
/// command to type. It was written because <c>ks12-3-owner-runbook.md</c> — the same document, one
/// era earlier, in the same shape — had six of its seven acts go unperformed with nothing anywhere
/// saying so. A checklist a person carries out is a checklist that silently is not.</para>
///
/// <para><b>So the checklist runs.</b> Every precondition DV7.3 measured by hand is measured here,
/// one named line each, and the verb <b>exits non-zero when any line is red</b>. Nothing it reports
/// is read off a document: the merge is two <c>git rev-list</c> counts, the CHANGELOG verdict is
/// <c>tools/changelog-section.sh</c>'s own exit code (the script <c>release.yml</c> runs as the first
/// job of a tag build), the process line is the same <c>UpdateSafety</c> detector <c>update</c>
/// refuses on, and the schema line compares the installed engine's commit against the migrations in
/// this tree.</para>
///
/// <para><b>It writes nothing and it dials nothing.</b> The store is opened read-only, the courier is
/// read from the scheduler and its settings file rather than from Telegram — one <c>getUpdates</c>
/// consumer per token, and the live courier owns it — and no act is performed. Performing the
/// mechanical acts is CH4.2's; this verb's job is to be unable to lie about whether they can be.</para>
/// </summary>
public sealed partial class ReleaseCommand : AsyncCommand<ReleaseCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: preflight, perform. Omit to show help.")]
        public string Verb { get; init; } = "";

        /// <summary>Named <c>--tag</c> and not <c>--version</c> on purpose: Spectre already owns
        /// <c>--version</c> at the application level, and the number being asked about is precisely
        /// the one that becomes <c>v&lt;x.y.z&gt;</c> on the releases page.</summary>
        [CommandOption("--tag <VERSION>")]
        [Description("The version being released, e.g. 0.6.0 (a leading v is fine). Omitted, the changelog line stops and names you.")]
        public string? Tag { get; init; }

        [CommandOption("--base <BRANCH>")]
        [Description("The branch being merged INTO (default: master)")]
        public string? Base { get; init; }

        [CommandOption("--branch <BRANCH>")]
        [Description("The branch being merged (default: the checked-out one)")]
        public string? Branch { get; init; }

        [CommandOption("--repo <DESTINATION>")]
        [Description("GitHub destination for the backfill line, as owner then slash then name. Default: the plan's github.repo.")]
        public string? Repo { get; init; }

        /// <summary>Dry run is the DEFAULT for <c>perform</c>, and this is what turns it off. An
        /// era-close that acted by default would be a verb whose first mistake is unrecoverable.</summary>
        [CommandOption("--yes")]
        [Description("perform the mechanical acts. Without it, `release perform` only says what it would do.")]
        public bool Yes { get; init; }

        [CommandOption("--history <DIR>")]
        [Description("Where the era's documents join the record (default: docs/history)")]
        public string? History { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Verb.Trim().ToLowerInvariant() switch
        {
            "preflight" => await PreflightAsync(settings).ConfigureAwait(false),
            "perform" => await PerformAsync(settings).ConfigureAwait(false),
            "" => Help(),
            var other => Unknown(other),
        };
    }

    private static int Help()
    {
        AnsiConsole.MarkupLine("[bold aqua]conductor release[/] — the era-close, measured instead of written.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [yellow]conductor release preflight[/] [grey][[--tag 0.6.0]] [[--base master]] [[--branch feat/x]] [[--repo owner/name]][/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]Six preconditions, one verdict each, and a non-zero exit when any is red:[/]");
        AnsiConsole.MarkupLine("  [grey]merge (is it a fast-forward), changelog (does the tag build have a section),[/]");
        AnsiConsole.MarkupLine("  [grey]processes (is a binary swap safe), migration (schema skew, trap 18),[/]");
        AnsiConsole.MarkupLine("  [grey]courier (would it survive the reinstall), backfill (which run is owed a record).[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]Exit 0 all green · 1 something is red · 2 nothing red, something is yours to decide.[/]");
        AnsiConsole.MarkupLine("  [grey]It writes nothing, tags nothing and merges nothing.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [yellow]conductor release perform[/] [grey][[--tag 0.6.0]] [[--yes]] [[--history docs/history]][/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]Four mechanical acts, each its own commit: the CHANGELOG rename, the ff-only merge,[/]");
        AnsiConsole.MarkupLine("  [grey]the tag, and the doc move WITH the plan repointed at it. Five owner acts named and[/]");
        AnsiConsole.MarkupLine("  [grey]stopped at: the version number, single-vs-split, the corpus, the reinstall, the push.[/]");
        AnsiConsole.MarkupLine("  [grey]Dry run unless --yes. Refuses outright while a run is live in the plan.[/]");
        return 1;
    }

    private static int Unknown(string verb)
    {
        AnsiConsole.MarkupLine($"[red]unknown sub-command '{Markup.Escape(verb)}'.[/] the sub-commands are [yellow]preflight[/] and [yellow]perform[/].");
        return 1;
    }

    private static async Task<int> PreflightAsync(Settings settings)
    {
        var sw = Stopwatch.StartNew();

        string planPath;
        PlanConfig plan;
        try
        {
            planPath = settings.ResolvePlanPath();
            plan = PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            // Same shape as the launch drill's: a plan that does not load is a finding, not a stack
            // trace and a crash log in whatever directory the operator happened to be standing in.
            AnsiConsole.MarkupLine("[bold aqua]conductor release preflight[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Render(new ReleaseCheck(ReleasePreflight.MergeCheck, ReleaseCheck.Fail, ex.Message, [])));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]NOT READY[/] — the plan does not load, so nothing was measured");
            return 1;
        }

        var repo = Directory.Exists(plan.Repo) ? plan.Repo : Directory.GetCurrentDirectory();
        var tag = settings.Tag?.Trim();

        AnsiConsole.MarkupLine($"[bold aqua]conductor release preflight[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine($"repo: {Markup.Escape(repo)}" + (tag is { Length: > 0 } ? $"  ·  release: {Markup.Escape(tag)}" : "  ·  release: [yellow]unnamed[/]"));
        AnsiConsole.WriteLine();

        var checks = await MeasureAsync(plan, planPath, repo, settings).ConfigureAwait(false);
        sw.Stop();

        foreach (var check in checks)
        {
            AnsiConsole.MarkupLine(Render(check));
            foreach (var line in check.Detail)
                AnsiConsole.MarkupLine($"           [grey]{Markup.Escape(line)}[/]");
        }

        var exit = ReleasePreflight.ExitCode(checks);
        var colour = exit switch { 0 => "green", 1 => "red", _ => "yellow" };
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[{colour}]{Markup.Escape(ReleasePreflight.Verdict(checks))}[/] ({sw.Elapsed.TotalMilliseconds:0}ms)");
        if (exit != 0)
            AnsiConsole.MarkupLine("[grey]nothing was merged, tagged, installed or pushed — this verb only measures.[/]");
        return exit;
    }

    /// <summary>The six lines, in order. Each probe is independent: one that cannot measure returns a
    /// red line saying so rather than throwing, because a preflight that dies on its third check has
    /// told the operator less than one that reports three reds.</summary>
    internal static async Task<IReadOnlyList<ReleaseCheck>> MeasureAsync(
        PlanConfig plan, string planPath, string repo, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);

        var store = PeekStore(plan, planPath);

        // One `version --json` for two lines. The process line needs the installed engine's real
        // BINARY (the PATH entry is a shim on this machine) and the migration line needs its COMMIT;
        // asking twice would be two subprocess launches for one answer, and two chances to disagree.
        var installed = InstalledStamp(repo);

        return
        [
            ReleasePreflight.Merge(ProbeMerge(repo, settings.Base, settings.Branch)),
            ReleasePreflight.Changelog(ProbeChangelog(repo, settings.Tag)),
            ReleasePreflight.Processes(ProbeProcesses(plan, planPath, installed)),
            ReleasePreflight.Migration(ProbeMigration(repo, store, installed)),
            ReleasePreflight.Courier(await ProbeCourierAsync(plan).ConfigureAwait(false)),
            ReleasePreflight.Backfill(ProbeBackfill(plan, settings.Repo, store)),
        ];
    }

    private static string Render(ReleaseCheck check)
    {
        var (glyph, colour) = check.State switch
        {
            ReleaseCheck.Ok => ("✓", "green"),
            ReleaseCheck.Owner => ("?", "yellow"),
            _ => ("✗", "red"),
        };
        return $"[{colour}]{glyph}[/] [bold]{Markup.Escape(check.Name),-10}[/] {Markup.Escape(check.Headline)}";
    }
}
