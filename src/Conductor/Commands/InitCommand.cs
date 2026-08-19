using System.ComponentModel;
using Conductor.Core;
using Conductor.Core.Interop;
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
        foreach (var (path, content) in TemplateScaffold(templatesDir))
            File.WriteAllText(path, content, System.Text.Encoding.UTF8);

        // KS8.2 — the AGENTS.md courtesy, and the import that makes Claude Code honour it. The I/O
        // sits here rather than in a helper because MA0045 exempts this override and not a helper,
        // and because keeping every write on one screen is how "never clobber" stays checkable.
        var courtesy = new List<string>();
        var agentsPath = Path.Combine(outputDir, AgentsFile.AgentsFileName);
        if (File.Exists(agentsPath))
        {
            courtesy.Add($"[grey]Kept[/] {Markup.Escape(agentsPath)} [grey](already yours — not touched)[/]");
        }
        else
        {
            File.WriteAllText(agentsPath, AgentsFile.Generate(name, "TRACKER.md"), System.Text.Encoding.UTF8);
            courtesy.Add($"[green]Created[/] {Markup.Escape(agentsPath)} [grey](the AGENTS.md convention)[/]");
        }

        var claudePath = Path.Combine(outputDir, AgentsFile.ClaudeFileName);
        var existingClaude = File.Exists(claudePath) ? File.ReadAllText(claudePath) : null;
        if (AgentsFile.ClaudeMdWithImport(existingClaude) is { } claudeMd)
        {
            File.WriteAllText(claudePath, claudeMd, System.Text.Encoding.UTF8);
            courtesy.Add(existingClaude is null
                ? $"[green]Created[/] {Markup.Escape(claudePath)} [grey]({AgentsFile.ImportLine} — Claude Code does not read AGENTS.md natively)[/]"
                : $"[green]Appended[/] {AgentsFile.ImportLine} [grey]to[/] {Markup.Escape(claudePath)}");
        }
        else
        {
            courtesy.Add($"[grey]Kept[/] {Markup.Escape(claudePath)} [grey](already imports {AgentsFile.ImportLine})[/]");
        }

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
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(templatesDir)}{Path.DirectorySeparatorChar} " +
            $"({PromptBuilder.BuiltInNames.Length} templates: {string.Join(", ", PromptBuilder.BuiltInNames)} — edit these)");
        foreach (var line in courtesy) AnsiConsole.MarkupLine(line);

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

    /// <summary>SF6.3 — "templates as content" means ALL of them. init used to drop session.md and
    /// fix.md and stop, which reads as "these are the templates": an operator who edited session.md
    /// had no way to learn that verify, audit, review, resume, advisor and chat were still rendering
    /// from C# string literals they could not see. Every name <see cref="PromptBuilder.ResolveTemplatePath"/>
    /// honours is written here, so the directory listing IS the answer to "what can I change?".</summary>
    internal static IReadOnlyList<(string Path, string Content)> TemplateScaffold(string templatesDir) =>
        [.. PromptBuilder.BuiltInNames.Select(t => (Path.Combine(templatesDir, t), PromptBuilder.BuiltIn(t)))];

    /// <summary>KS3.1 — the agent CLI a scaffold names when nothing better is known: the one
    /// <c>init</c> has always written. <see cref="PlanNewCommand.ResolveAgentCommand"/> reaches for it
    /// last, after asking PATH.</summary>
    internal const string DefaultAgentCommand = "opencode";

    /// <summary>KS3.1 — the <c>agent</c> block body for a named CLI, indented for
    /// <see cref="BuildPlanJson"/>. The COMMAND is written through verbatim (an operator may name a
    /// path, not a bare name); what the name selects is the argument shape, because a CLI named
    /// without the flags it needs is worse than one not named at all. Anything the table does not know
    /// gets the opencode shape, which is what <c>init</c> has always written — that is why <c>init</c>
    /// is byte-identical after this change.</summary>
    internal static string AgentBlockBody(string command)
    {
        var quoted = JsonString(command);
        return Path.GetFileNameWithoutExtension(command).Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? $$"""
                    "command": "{{quoted}}",
                    // A conductor session edits files with nobody watching, and headless claude denies
                    // every tool prompt it cannot ask about — so the scaffold ships the skip-permissions
                    // flag. Delete it if you would rather approve each tool yourself, and plan to sit
                    // with the run when you do.
                    "args": ["-p", "{prompt}", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions", "--session-id", "{sessionId}"],
                    "provider": "claude"
                """
            : $$"""
                    "command": "{{quoted}}",
                    "args": ["run", "{prompt}"],
                    "provider": "opencode"
                """;
    }

    /// <summary>A command as it must appear inside a JSON string: a Windows path is full of backslashes
    /// and a plan that fails to parse is a scaffold that deletes itself at the self-check.</summary>
    private static string JsonString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>W4.2's commented advisor hint, verbatim — lifted out of the plan template only so the
    /// live block (KS3.1) can take its place without the template growing a second copy of everything
    /// around it. <c>init</c> emits exactly this, exactly here, as it always has.</summary>
    private const string CommentedAdvisorHint = """
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
        """;

    /// <summary>KS3.1 — the advisor block, live rather than commented, for the one case where the
    /// operator has asked for a model by name (<c>plan new --advisor</c>). Everything else keeps the
    /// commented hint: the advisor is consulted mid-run too, so switching it on is a spend decision and
    /// belongs to whoever typed the flag, not to a scaffold.</summary>
    internal static string AdvisorBlockBody(string command) => $$"""
          "advisor": {
            "enabled": true,
            "command": "{{JsonString(command)}}",
            "args": ["-p", "{prompt}", "--model", "{model}"],
            "output": "text",
            "timeoutMinutes": 6
          },
        """;

    internal static string BuildPlanJson(
        string name, string repo, RepoKind kind, string? agentCommand = null, string? advisorCommand = null)
    {
        var repoNorm = repo.Replace("\\", "/");
        var agentBody = AgentBlockBody(
            string.IsNullOrWhiteSpace(agentCommand) ? DefaultAgentCommand : agentCommand).TrimEnd();
        var advisorBlock = string.IsNullOrWhiteSpace(advisorCommand)
            ? CommentedAdvisorHint
            : AdvisorBlockBody(advisorCommand.Trim()).TrimEnd();
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
        {{agentBody}}
          },

        {{advisorBlock}}

          // SF6.3 — where the run reaches you when you are not at the desk. The bot token is read from
          // CONDUCTOR_TELEGRAM_TOKEN (or saved from the Face's Telegram tab) and is never written here,
          // so this block is safe to commit. Chat ids are numeric — @userinfobot tells you yours.
          // Push-only until you list one; "enableTwoWay" then lets you approve and steer from the phone.
          //
          // "telegram": {
          //   "allowedChatIds": ["123456789"],
          //   "enableTwoWay": true,
          //   "pollIntervalSeconds": 4
          // },

          // SF6.3 — the babysitter, named in the plan instead of remembered in a shell history. Invoked
          // only when the run wakes something (a park, an owner gate, a circuit breaker), with the wake
          // brief on stdin — quiet nights cost nothing. "standingOrders" rides INTO that brief, so the
          // agent reads its authority on the same stdin as the wake; unstated reads as "escalate
          // everything". "maxPerHour" is a cost fuse, not a nicety: a run that parks, is resumed and
          // parks again on the same cause is a model invocation every few seconds until someone notices.
          //
          // "supervisor": {
          //   "enabled": true,
          //   "command": "claude -p \"You are the night watch. The wake brief is on stdin.\"",
          //   "timeoutMinutes": 10,
          //   "maxPerHour": 6,
          //   "standingOrders": "You may: approve an owner gate whose checkpoint has evidence; inject a hint on a circuit breaker. You must escalate: anything that spends money, any merge, any plan edit."
          // },

          // A spend cap so the first run cannot surprise you: at the cap the run parks at AwaitingOwner
          // and waits, it does not die. Raise it, swap it for "maxRunTokens", or delete the block if
          // unbounded is deliberate — but delete it on purpose, not by never having had one.
          "limits": {
            "maxRunCostUsd": 25.0
          },

          // Domain packs — reusable context (house style, the mistakes agents make here) appended to
          // every session prompt from templates/packs/<name>.md. Left off deliberately: a pack is
          // thousands of characters on top of the prompt, and on Windows a composed prompt past ~8k
          // hands the agent CLI a command line the OS refuses. Add one when you have measured the room.
          //
          // "packs": ["house-style"],

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
