using System.ComponentModel;
using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// M8.2 (design doc) — <c>conductor init</c>: scaffold a runnable plan, an editable templates dir, and
/// a TRACKER, with gate commands chosen from the detected repo type. A superset of <c>new-plan</c>
/// (which writes only a minimal plan + tracker): init detects dotnet/node/go/rust/python, wires the
/// matching build+test gates, and drops editable copies of the built-in prompt templates so
/// "templates as content" works the moment you run. Self-checks that the scaffold loads before
/// leaving it on disk.
/// </summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <DIR>")]
        [Description("Directory to scaffold into. Created if missing. Default: cwd.")]
        public string? Output { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Plan name. Default: the directory name.")]
        public string? Name { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Absolute path to the repo to drive. Default: the output directory.")]
        public string? Repo { get; init; }

        [CommandOption("--from-idea <TEXT_OR_FILE>")]
        [Description("Scaffold, then turn this idea (quoted prose, or a path to a doc) into stages and checkpoints — one command from idea to drivable plan.")]
        public string? FromIdea { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description("Model the advisor uses to interpret --from-idea prose (fills the advisor's {model} placeholder). Ignored for a structured doc, which needs no model.")]
        public string? Model { get; init; }
    }

    // W4.1: detection moved to Core (RepoKindDetector) so `plan import` proposes the same gates
    // from the same signal. These forwarders keep init's call sites and tests unchanged.
    internal static RepoKind DetectRepoKind(string repo) => RepoKindDetector.Detect(repo);

    internal static (string build, string tests) GatesFor(RepoKind kind) => RepoKindDetector.GatesFor(kind);

    public override int Execute(CommandContext context, Settings settings)
    {
        var outputDir = Path.GetFullPath(settings.Output ?? ".");
        var name = settings.Name
            ?? Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "plan";
        var repo = settings.Repo ?? outputDir;

        Directory.CreateDirectory(outputDir);

        var planPath = Path.Combine(outputDir, "conductor.plan.json");
        var trackerPath = Path.Combine(outputDir, "TRACKER.md");
        var templatesDir = Path.Combine(outputDir, "templates");

        if (File.Exists(planPath) || File.Exists(trackerPath))
        {
            AnsiConsole.MarkupLine("[red]conductor.plan.json / TRACKER.md already exist here[/] — delete them or pick another --output.");
            return 1;
        }

        var kind = DetectRepoKind(repo);

        File.WriteAllText(planPath, BuildPlanJson(name, repo, kind), System.Text.Encoding.UTF8);
        File.WriteAllText(trackerPath, BuildTrackerMd(name), System.Text.Encoding.UTF8);

        Directory.CreateDirectory(templatesDir);
        File.WriteAllText(Path.Combine(templatesDir, "session.md"), PromptBuilder.BuiltIn("session.md"), System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(templatesDir, "fix.md"), PromptBuilder.BuiltIn("fix.md"), System.Text.Encoding.UTF8);

        // Self-check: don't leave a scaffold that won't load. Mirror NewPlanCommand's A6 discipline.
        try
        {
            PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            File.Delete(planPath);
            File.Delete(trackerPath);
            try { Directory.Delete(templatesDir, recursive: true); } catch (IOException) { }
            AnsiConsole.MarkupLine($"[red]Scaffold failed self-check and was removed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Detected[/] {kind.ToString().ToLowerInvariant()} repo");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(planPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(trackerPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(templatesDir)}{Path.DirectorySeparatorChar} (session.md, fix.md — edit these)");

        if (!string.IsNullOrWhiteSpace(settings.FromIdea))
            return FromIdea(planPath, settings.FromIdea, settings.Model);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: edit the example stage in [aqua]conductor.plan.json[/], then [aqua]conductor doctor[/] and [aqua]conductor run[/].");
        return 0;
    }

    /// <summary>
    /// W4.2: the second half of one command — the scaffold exists, now make it about the idea.
    ///
    /// Until this, the documented bootstrap (`init` then `plan import`) could not use the AI path at
    /// all: init wrote no advisor block, and both prose ingresses hard-refuse without one. And the
    /// Face — where you would naturally type an idea — only attaches to a running control plane,
    /// which needs a plan that already exists. So the first mile had no entrance.
    ///
    /// Structured docs still cost nothing (the deterministic parser); prose goes to the advisor.
    /// Either way the scaffold's placeholder stage steps aside once real stages arrive.
    /// </summary>
    internal static int FromIdea(string planPath, string idea, string? model)
    {
        AnsiConsole.WriteLine();
        var code = PlanImportCommand.ExecuteImport(planPath, idea, model, assumeYes: true);
        if (code != 0)
        {
            AnsiConsole.MarkupLine("[yellow]The scaffold is on disk, but the idea was not turned into stages.[/]");
            AnsiConsole.MarkupLine("[grey]Uncomment the advisor block in conductor.plan.json (or pass a structured plan doc), " +
                                  "then: conductor plan import \"<your idea>\"[/]");
            return code;
        }

        var plan = PlanConfig.Load(planPath);
        if (DropPlaceholderStage(plan)) plan.Save();
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Plan built from your idea[/] — {plan.Stages.Count} stage(s), " +
            $"{plan.Progress?.Checkpoints?.Count ?? 0} checkpoint(s).");
        AnsiConsole.MarkupLine("Next: [aqua]conductor doctor[/], then [aqua]conductor run[/] (add [aqua]--paused[/] to look before it moves).");
        return 0;
    }

    /// <summary>The scaffold's "rename me" stage is a starting point, not content. Once an idea has
    /// produced real stages it is noise on the board — dropped along with its placeholder row, but
    /// only if nothing has claimed it yet. Returns true when the plan changed (the caller saves).</summary>
    internal static bool DropPlaceholderStage(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Stages.Count <= 1) return false;
        var placeholder = plan.Stages.FirstOrDefault(s =>
            s.Id == PlaceholderStageId && (s.Title ?? "").Contains("rename me", StringComparison.OrdinalIgnoreCase));
        if (placeholder is null) return false;

        var declared = plan.Progress?.Checkpoints ?? [];
        if (declared.Any(c => c.Id.StartsWith(PlaceholderStageId + ".", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(c.Status, "TODO", StringComparison.OrdinalIgnoreCase)))
            return false;

        plan.Stages.Remove(placeholder);
        if (plan.Progress?.Checkpoints is { } cps)
            plan.Progress.Checkpoints = [.. cps.Where(c =>
                !c.Id.StartsWith(PlaceholderStageId + ".", StringComparison.OrdinalIgnoreCase))];
        foreach (var s in plan.Stages)
            if (s.DependsOn is { Count: > 0 } deps && deps.Contains(PlaceholderStageId, StringComparer.OrdinalIgnoreCase))
                s.DependsOn = [.. deps.Where(d => !string.Equals(d, PlaceholderStageId, StringComparison.OrdinalIgnoreCase))];
        return true;
    }

    internal const string PlaceholderStageId = "S1";

    internal static string BuildPlanJson(string name, string repo, RepoKind kind)
    {
        var repoNorm = repo.Replace("\\", "/");
        var (build, tests) = GatesFor(kind);
        var gates = (build.Length == 0 && tests.Length == 0)
            ? "[]"
            : $$"""
              [
                { "name": "build", "command": "{{build}}", "tier": "fast", "timeoutMinutes": 10 },
                { "name": "tests", "command": "{{tests}}", "tier": "full", "timeoutMinutes": 20 }
              ]
              """;
        return $$"""
        {
          "name": "{{name}}",
          "repo": "{{repoNorm}}",
          "tracker": "TRACKER.md",
          "templatesDir": "templates",
          "agent": {
            "command": "opencode",
            "args": ["run", "{prompt}"],
            "provider": "opencode"
          },

          // W4.2 — the advisor. A model consulted only where you ask for one: turning prose into a
          // plan (`conductor init --from-idea "…"`, `conductor plan import "…"`), refining or
          // splitting a card, and judging a stuck stage. Never inside scheduling — the loop stays
          // deterministic. A structured plan document needs none of this; it parses for free.
          // Uncomment and point it at any CLI that answers on stdout. Omit "args" and you get the
          // shipped default, ["-p", "{prompt}"]; an unfilled "{model}" (and the flag before it) is
          // dropped, so this block works with or without `plan import --model`.
          //
          // "advisor": {
          //   "enabled": true,
          //   "command": "claude",
          //   "args": ["-p", "{prompt}", "--output-format", "json", "--model", "{model}"],
          //   "output": "json",
          //   "timeoutMinutes": 6
          // },

          "stages": [
            {
              "id": "S1",
              "title": "First stage — rename me and describe the work",
              "sessions": 2,
              "notes": "Replace this with what the stage should deliver. Each session reads TRACKER.md, does the next checkpoint, runs the gate battery, and commits."
            }
          ],
          "gates": {{gates}}
        }
        """;
    }

    internal static string BuildTrackerMd(string name) => $$"""
        # {{name}} — TRACKER

        ## Handoff (overwrite this block each session, <=12 lines, no history)
        last: none. Status: idle.

        ## Checkpoints

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | S1.1 | first checkpoint — rename me | TODO |  |  |

        """;
}
