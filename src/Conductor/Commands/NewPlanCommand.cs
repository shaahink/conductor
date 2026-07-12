using System.ComponentModel;
using System.Text.Json;

using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>Scaffolds a new plan + TRACKER.md (B1.6).</summary>
public sealed class NewPlanCommand : Command<NewPlanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <DIR>")]
        [Description("Directory to create the files in. Created if missing. Default: cwd.")]
        public string? Output { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Plan name. Default: directory name or 'plan'.")]
        public string? Name { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Absolute path to the repo. Default: output directory.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var outputDir = Path.GetFullPath(settings.Output ?? ".");
        var name = settings.Name ?? Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "plan";
        var repo = settings.Repo ?? outputDir;

        Directory.CreateDirectory(outputDir);

        var planPath = Path.Combine(outputDir, "conductor.plan.json");
        var trackerPath = Path.Combine(outputDir, "TRACKER.md");

        if (File.Exists(planPath) || File.Exists(trackerPath))
        {
            AnsiConsole.MarkupLine("[red]Plan file(s) already exist — delete them first or use a different output directory.[/]");
            return 1;
        }

        File.WriteAllText(planPath, BuildMinimalPlanJson(name, repo), System.Text.Encoding.UTF8);
        File.WriteAllText(trackerPath, BuildMinimalTrackerMd(name), System.Text.Encoding.UTF8);

        // Verify the output loads (A6 ship-without-launch). Don't leave a half-written scaffold on
        // disk if the self-check fails — clean up and surface the reason.
        try
        {
            PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            File.Delete(planPath);
            File.Delete(trackerPath);
            AnsiConsole.MarkupLine($"[red]Scaffold failed self-check and was removed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(planPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(trackerPath)}");
        return 0;
    }

    internal static string BuildMinimalPlanJson(string name, string repo)
    {
        var repoNormalised = repo.Replace("\\", "/");
        return $$"""
        {
          "version": "1.0",
          "name": "{{name}}",
          "repo": "{{repoNormalised}}",
          "tracker": "TRACKER.md",
          "agent": {
            "command": "opencode",
            "args": ["run", "{prompt}"],
            "provider": "opencode"
          },
          "stages": []
        }
        """;
    }

    internal static string BuildMinimalTrackerMd(string name)
    {
        return $$"""
        # {{name}} — TRACKER

        ## Handoff
        last: none. Status: idle.

        """;
    }
}
