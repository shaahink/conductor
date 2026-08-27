using System.Globalization;

using Conductor.Core.Release;
using Conductor.Models;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// CH4.4 — the runbook stops being a document a session writes and becomes what the two verbs above
/// already know.
///
/// <para><b>The failure being retired.</b> <c>.conductor/evidence/KS12/ks12-3-owner-runbook.md</c>
/// and <c>DV7/dv7-3-owner-runbook.md</c> were hand-written a fortnight apart, in the same shape, and
/// the second one's first finding was that the first one had not been carried out: `master` had been
/// fast-forwarded and then nothing else in it happened — no tag, no CHANGELOG rename, no doc move,
/// no backfill. Six of seven acts, unperformed, unnoticed, for an era. A document written by hand
/// cannot know whether the acts in it were done, and cannot know whether an act has been added since
/// it was written. This one is regenerated from the measurements every time it is asked for.</para>
///
/// <para><b>It performs nothing, and it does not refuse a live run.</b> <c>perform</c> stops dead
/// while a conductor holds the plan's state directory, because it rewrites the CHANGELOG and the
/// plan itself. This verb only measures and renders, and mid-run is precisely when the owner wants
/// to read what the close will involve — refusing there would be caution costing the reader the one
/// thing they came for.</para>
/// </summary>
public sealed partial class ReleaseCommand
{
    private static async Task<int> RunbookAsync(Settings settings)
    {
        string planPath;
        PlanConfig plan;
        try
        {
            planPath = settings.ResolvePlanPath();
            plan = PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[red]the plan does not load:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var repo = Directory.Exists(plan.Repo) ? plan.Repo : Directory.GetCurrentDirectory();

        // The same two calls the two verbs make, with nothing performed: `RunActsAsync` in dry-run
        // plans every mechanical act and appends the owner acts, and touches nothing on the way.
        var checks = await MeasureAsync(plan, planPath, repo, settings).ConfigureAwait(false);
        var acts = await RunActsAsync(plan, planPath, repo, settings, dryRun: true).ConfigureAwait(false);

        var merge = ProbeMerge(repo, settings.Base, settings.Branch);
        var installed = InstalledStamp(repo);

        var document = ReleaseRunbook.Render(new RunbookFacts(
            PlanName: plan.Name,
            Repo: repo,
            Branch: merge.Branch,
            BaseBranch: merge.BaseBranch,
            Tag: settings.Tag?.Trim(),
            InstalledEngine: Stamp(installed),
            GeneratedUtc: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Checks: checks,
            Acts: acts));

        var exit = ReleasePreflight.ExitCode(checks);
        if (settings.Out is not { Length: > 0 } outPath)
        {
            // Written raw, NOT through the markup renderer: the document is markdown full of square
            // brackets and backticks, and Spectre would eat half of it looking for colours.
            AnsiConsole.WriteLine(document);
            return exit;
        }

        var full = Path.GetFullPath(outPath, repo);
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(full, document).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]could not write the runbook:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]runbook written[/] {Markup.Escape(full)} " +
            $"[grey]({Markup.Escape(checks.Count.ToString(CultureInfo.InvariantCulture))} preconditions, " +
            $"{Markup.Escape(acts.Count.ToString(CultureInfo.InvariantCulture))} acts)[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ReleasePreflight.Verdict(checks))}[/]");
        AnsiConsole.MarkupLine("[grey]nothing was merged, tagged, moved, installed or pushed.[/]");
        return exit;
    }

    /// <summary>What the engine on PATH says it is — the binary the reinstall replaces, not this
    /// process. Blank rather than a guess when <c>version --json</c> could not be asked.</summary>
    private static string Stamp(EngineStampProbe installed) =>
        installed.Version is { Length: > 0 } v
            ? v + (installed.Sha is { Length: > 0 } sha ? "+" + sha : "") + (installed.Dirty ? " (dirty)" : "")
            : "";
}
