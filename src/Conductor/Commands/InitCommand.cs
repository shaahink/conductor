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
    }

    internal enum RepoKind { Generic, Dotnet, Node, Go, Rust, Python }

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
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: edit the example stage in [aqua]conductor.plan.json[/], then [aqua]conductor doctor[/] and [aqua]conductor run[/].");
        return 0;
    }

    /// <summary>Cheapest reliable signal: the presence of a build-system marker file at the repo root.</summary>
    internal static RepoKind DetectRepoKind(string repo)
    {
        if (!Directory.Exists(repo)) return RepoKind.Generic;
        bool Any(params string[] globs) => globs.Any(g =>
            g.Contains('*')
                ? Directory.EnumerateFiles(repo, g, SearchOption.TopDirectoryOnly).Any()
                : File.Exists(Path.Combine(repo, g)));

        if (Any("*.sln", "*.slnx", "*.csproj", "*.fsproj")) return RepoKind.Dotnet;
        if (Any("go.mod")) return RepoKind.Go;
        if (Any("Cargo.toml")) return RepoKind.Rust;
        if (Any("package.json")) return RepoKind.Node;
        if (Any("pyproject.toml", "setup.py", "requirements.txt")) return RepoKind.Python;
        return RepoKind.Generic;
    }

    internal static (string build, string tests) GatesFor(RepoKind kind) => kind switch
    {
        RepoKind.Dotnet => ("dotnet build", "dotnet test"),
        RepoKind.Node => ("npm run build", "npm test"),
        RepoKind.Go => ("go build ./...", "go test ./..."),
        RepoKind.Rust => ("cargo build", "cargo test"),
        RepoKind.Python => ("python -m compileall -q .", "pytest -q"),
        _ => ("", ""),
    };

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
