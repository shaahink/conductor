using System.Text;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// KS3.1 — <c>conductor plan new</c>: one command from an empty repo to a plan the engine will drive,
/// with the JSON never opened in an editor.
///
/// <para>The pieces already existed and did not meet: <c>init</c> wrote a scaffold whose agent block
/// named a CLI that may not be installed here and whose templates spelled the escalation token, so
/// <c>doctor</c> answered "1 fail" on a file the operator had just been handed; <c>plan import</c>
/// turned an idea into stages but only against a plan that already existed. This is the join, and the
/// bar it is held to is doctor's own: from an empty git repo, one invocation, <c>0 fail</c>.</para>
///
/// <para>What it does NOT do is spend money it was not asked to. A structured plan or tracker document
/// takes <see cref="PlanImportService.ParseStructured"/> and never spawns a model; only freeform prose
/// consults the advisor, and with no advisor configured the command says so and leaves the scaffold —
/// loadable, never half-written — on disk.</para>
/// </summary>
#pragma warning disable MA0045 // sync file I/O at the Spectre.Cli sync boundary (same pattern as InitCommand/GateCommand)
public static class PlanNewCommand
{
    /// <summary>The agent CLIs a scaffold knows how to invoke, in preference order. Short on purpose:
    /// a name here is a promise that <see cref="InitCommand.AgentBlockBody"/> writes the flags that
    /// CLI actually needs. Anything else the operator names with <c>--agent</c> is written verbatim
    /// with the shipped default's argument shape.</summary>
    internal static readonly string[] AgentCandidates = ["claude", "opencode"];

    /// <summary>Which agent CLI this machine actually has, preferring <see cref="AgentCandidates"/> in
    /// order. Falls back to the documented default so the scaffold is still a complete plan on a
    /// machine with no agent installed — doctor then says the true thing ("not found on PATH") instead
    /// of the accidental thing ("we guessed wrong").
    /// <para><paramref name="onPath"/> is the seam: doctor resolves PATH for real, a test hands in its
    /// own probe rather than mutating the process environment out from under its neighbours.</para></summary>
    internal static string ResolveAgentCommand(Func<string, bool>? onPath = null)
    {
        var probe = onPath ?? IsOnPath;
        foreach (var candidate in AgentCandidates)
            if (probe(candidate)) return candidate;
        return InitCommand.DefaultAgentCommand;
    }

    public static int Execute(PlanCommand.Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var outputDir = Path.GetFullPath(settings.Output ?? ".");
        var name = settings.Name
            ?? Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "plan";
        var repo = settings.Repo ?? outputDir;

        var planPath = Path.Combine(outputDir, "conductor.plan.json");
        var trackerPath = Path.Combine(outputDir, "TRACKER.md");
        if (File.Exists(planPath) || File.Exists(trackerPath))
        {
            AnsiConsole.MarkupLine("[red]conductor.plan.json / TRACKER.md already exist here[/] — delete them, pick another --output, " +
                "or feed the idea to the plan you have: [aqua]conductor plan import \"…\"[/].");
            return 1;
        }

        // The interview, such as it is: one question, asked only when there is a human at a terminal to
        // answer it. Everything else about this command is one-shot on purpose — a scaffold that stops
        // to chat is a scaffold no script can call.
        var idea = FirstNonBlank(settings.FromIdea, settings.Key) ?? AskForTheIdea();

        var agentCommand = string.IsNullOrWhiteSpace(settings.Agent) ? ResolveAgentCommand() : settings.Agent!.Trim();
        var advisorCommand = string.IsNullOrWhiteSpace(settings.Advisor) ? null : settings.Advisor!.Trim();
        var kind = InitCommand.DetectRepoKind(repo);

        if (!TryScaffold(outputDir, name, repo, kind, agentCommand, advisorCommand)) return 1;

        AnsiConsole.MarkupLine($"[green]Detected[/] {kind.ToString().ToLowerInvariant()} repo, agent [bold]{Markup.Escape(agentCommand)}[/]");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(planPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(trackerPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(Path.Combine(outputDir, "templates"))}{Path.DirectorySeparatorChar} " +
            $"({PromptBuilder.BuiltInNames.Length} templates: {string.Join(", ", PromptBuilder.BuiltInNames)} — edit these)");

        if (string.IsNullOrWhiteSpace(idea))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Next: describe the work with [aqua]conductor plan import \"<your idea>\"[/] " +
                "(or edit the example stage), then [aqua]conductor doctor[/] and [aqua]conductor run[/].");
            return 0;
        }

        // One implementation of "prose or document into stages", shared with `init --from-idea`: the
        // deterministic parser first, the advisor only for prose, and the placeholder stage stepping
        // aside once real stages arrive.
        var code = InitCommand.FromIdea(planPath, idea, settings.Model);
        if (code != 0)
        {
            if (advisorCommand is null)
                AnsiConsole.MarkupLine("[grey]Or say which model may read it: [/][aqua]conductor plan new --advisor <cli> --from-idea \"…\"[/]");
            return code;
        }

        RewriteTrackerRows(planPath, trackerPath);
        return 0;
    }

    /// <summary>Writes the four artefacts and refuses to leave any of them behind if the result will
    /// not load — <c>init</c>'s A6 discipline, applied to the whole set rather than to the plan alone.
    /// Internal so a test can drill the scaffold with a stated agent command instead of inheriting
    /// whichever CLI the machine running the suite happens to have installed.</summary>
    internal static bool TryScaffold(
        string outputDir, string name, string repo, RepoKind kind, string agentCommand, string? advisorCommand = null)
    {
        Directory.CreateDirectory(outputDir);
        var planPath = Path.Combine(outputDir, "conductor.plan.json");
        var trackerPath = Path.Combine(outputDir, "TRACKER.md");
        var templatesDir = Path.Combine(outputDir, "templates");

        File.WriteAllText(planPath, InitCommand.BuildPlanJson(name, repo, kind, agentCommand, advisorCommand), Encoding.UTF8);
        File.WriteAllText(trackerPath, InitCommand.BuildTrackerMd(name), Encoding.UTF8);
        Directory.CreateDirectory(templatesDir);
        foreach (var (path, content) in InitCommand.TemplateScaffold(templatesDir))
            File.WriteAllText(path, content, Encoding.UTF8);

        try
        {
            PlanConfig.Load(planPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            try { File.Delete(planPath); } catch (IOException) { }
            try { File.Delete(trackerPath); } catch (IOException) { }
            try { Directory.Delete(templatesDir, recursive: true); } catch (IOException) { }
            AnsiConsole.MarkupLine($"[red]Scaffold failed self-check and was removed:[/] {Markup.Escape(ex.Message)}");
            return false;
        }
    }

    /// <summary>The tracker is the human-readable view of the plan's declared work, so once an idea has
    /// replaced the placeholder stage the scaffold's <c>S1.1 — rename me</c> row is a lie in the one
    /// file an operator actually reads. Rewrites the checkpoint table from what the plan now declares
    /// and leaves everything else in the file — the handoff block above all — untouched.
    /// <para>No-op for a plan whose work is not declared inline (a <c>script</c> provider owns its own
    /// rows), and for a tracker that carries no table to replace.</para></summary>
    internal static void RewriteTrackerRows(string planPath, string trackerPath)
    {
        PlanConfig plan;
        try { plan = PlanConfig.Load(planPath); }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException) { return; }

        if (plan.Progress?.Checkpoints is not { Count: > 0 } declared) return;
        if (!File.Exists(trackerPath)) return;

        var text = File.ReadAllText(trackerPath);
        var marker = text.IndexOf(TableHeader, StringComparison.Ordinal);
        if (marker < 0) return;

        var table = new StringBuilder();
        table.Append(TableHeader).Append('\n').Append(TableRule).Append('\n');
        foreach (var c in declared)
            table.Append("| ").Append(c.Id).Append(" | ").Append(OneLine(c.Title)).Append(" | ")
                 .Append(string.IsNullOrWhiteSpace(c.Status) ? "TODO" : c.Status.Trim()).Append(" |  |  |\n");

        File.WriteAllText(trackerPath, string.Concat(text.AsSpan(0, marker), table.ToString(), "\n"), Encoding.UTF8);
    }

    private const string TableHeader = "| # | Checkpoint | Status | Commit | Evidence |";
    private const string TableRule = "|---|-----------|--------|--------|----------|";

    /// <summary>A checkpoint title is prose from a document; a newline or a pipe in it would end the
    /// row early and the provider would parse a different item than the plan declares.</summary>
    private static string OneLine(string? title) =>
        (title ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    /// <summary>The one question. Skipped whenever stdin or stdout is redirected, so a script, a test
    /// and a CI job all get the non-interactive path with no prompt to hang on.</summary>
    private static string? AskForTheIdea()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return null;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Describe what you want built — a sentence or two, or the path to a PRD or an existing tracker.[/]");
        AnsiConsole.MarkupLine("[grey]Blank scaffolds the plan and leaves the stages to you.[/]");
        var answer = AnsiConsole.Prompt(new TextPrompt<string>("[aqua]what are you building?[/]").AllowEmpty());
        return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
    }

    private static bool IsOnPath(string command)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("").ToArray()
            : [""];
        foreach (var dir in dirs)
        foreach (var ext in exts)
            if (File.Exists(Path.Combine(dir, command + ext))) return true;
        return false;
    }
}
#pragma warning restore MA0045
