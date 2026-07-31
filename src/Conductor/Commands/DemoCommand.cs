using System.ComponentModel;
using System.Globalization;

using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Models;

using Microsoft.Extensions.DependencyInjection;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// The credential-free front door: one command that drives a complete plan from nothing to a
/// finished run, on any platform, with no API key, no agent CLI, and no PowerShell.
///
/// This exists because the honest proof we already had — <c>tools/w5/rehearsal.ps1</c> — was
/// reachable only from Windows and only after a source build, which made "does this actually work?"
/// an expensive question for anyone evaluating the project. Everything here is real: a real git
/// repo, real gates with real exit codes, the real orchestrator loop, and the real
/// <c>conductor task --done</c> claim path. Only the coding agent is a stand-in
/// (<see cref="FakeAgentCommand"/>), because that is the only part that would cost money.
///
/// It is also the runtime proof that the engine is not Windows-only: the gates carry no
/// <c>shell</c>, so they resolve to the host's own (see <c>docs/platforms.md</c>).
/// </summary>
public sealed class DemoCommand : AsyncCommand<DemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <DIR>")]
        [Description("Where to build the demo repo. Default: a new temp directory, removed when done.")]
        public string? Output { get; init; }

        [CommandOption("--keep")]
        [Description("Keep the demo repo afterwards so you can poke at .conductor/ — plan, run.db, prompts, logs.")]
        public bool Keep { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var keep = settings.Keep || settings.Output is not null;
        var dir = Path.GetFullPath(settings.Output
            ?? Path.Combine(Path.GetTempPath(), $"conductor-demo-{Guid.NewGuid():N}"[..24]));

        AnsiConsole.MarkupLine("[bold]conductor demo[/] — a complete run against a built-in fake agent.");
        AnsiConsole.MarkupLine("[grey]No credentials, no spend. Everything but the coding agent is real.[/]");
        AnsiConsole.WriteLine();

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            AnsiConsole.MarkupLine("[red]Cannot locate this executable[/] — the demo spawns itself as the agent.");
            return 1;
        }

        try
        {
            AnsiConsole.MarkupLine($"[grey]1/3[/] building a throwaway repo at {Markup.Escape(dir)}");
            if (!await ScaffoldAsync(dir, exe).ConfigureAwait(false)) return 1;

            AnsiConsole.MarkupLine("[grey]2/3[/] driving the plan — 3 checkpoints across 2 stages");
            AnsiConsole.WriteLine();
            var (code, state, plan) = await DriveAsync(dir).ConfigureAwait(false);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]3/3[/] done — status [bold]{Markup.Escape(state.Status.ToString())}[/], " +
                $"{state.SessionCounter} session(s), exit {code}");
            Summarise(plan, dir, keep);
            return code;
        }
        finally
        {
            if (!keep) ForceDelete(dir);
        }
    }

    /// <summary>A real git repo with a real tracker and a plan pointed at the built-in agent.</summary>
    private static async Task<bool> ScaffoldAsync(string dir, string exe)
    {
        Directory.CreateDirectory(dir);
        var init = await GitAsync(dir, "init", "--quiet").ConfigureAwait(false);
        if (init.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]git init failed[/] — the demo verifies work by diffing commits, so git is required.");
            return false;
        }
        // Local config only: never touch the user's global identity for a throwaway repo.
        await GitAsync(dir, "config", "user.email", "demo@conductor.local").ConfigureAwait(false);
        await GitAsync(dir, "config", "user.name", "Conductor Demo").ConfigureAwait(false);
        await GitAsync(dir, "config", "commit.gpgsign", "false").ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"),
            "# Demo project\n\nA throwaway repo so `conductor demo` has something real to work on.\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, "TRACKER.md"), Tracker).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, "conductor.plan.json"), PlanJson(dir, exe)).ConfigureAwait(false);

        await GitAsync(dir, "add", "-A").ConfigureAwait(false);
        await GitAsync(dir, "commit", "-m", "chore: demo scaffold", "--no-gpg-sign", "--quiet").ConfigureAwait(false);
        return true;
    }

    private static async Task<(int Code, RunState State, PlanConfig Plan)> DriveAsync(string dir)
    {
        var planPath = Path.Combine(dir, "conductor.plan.json");
        var plan = PlanConfig.Load(planPath);
        Directory.CreateDirectory(plan.StateDir);

        var state = RunState.LoadOrNew(Path.Combine(plan.StateDir, "state.json"), plan.Name);
        state.RunId = Guid.NewGuid().ToString("N");

        using var cts = new CancellationTokenSource();
        InstallCancelHandler(cts);

        // No control plane and no Face: a first run should not open a port (and trip a firewall
        // prompt) or take over the terminal. maxSessions is a backstop, not the exit condition —
        // a healthy demo finishes because every checkpoint is confirmed.
        var opts = new RunOptions(DryRun: false, Once: false, MaxSessions: 16, ControlPlane: false, ControlPlanePort: 0, StartPaused: false);
        using var host = ConductorHost.Build(plan, state, new PlainSink(), opts, consoleSink: true);

        // SC1.1: the demo is a real run, so it gets the run path's real wiring. The scaffolded demo
        // plan has no Telegram block today, which makes this a no-op — but "every run path starts
        // its hosted services" is the invariant that stops the next one from silently regressing.
        await ConductorHost.StartRunServicesAsync(host, cts.Token).ConfigureAwait(false);
        int code;
        try
        {
            code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            await ConductorHost.StopRunServicesAsync(host, CancellationToken.None).ConfigureAwait(false);
        }
        return (code, state, plan);
    }

    private static void Summarise(PlanConfig plan, string dir, bool keep)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]What just happened[/]");
        AnsiConsole.MarkupLine("  The agent claimed each checkpoint with [aqua]conductor task --done[/] — the one claim path.");
        AnsiConsole.MarkupLine("  Conductor confirmed each one independently: gate exit codes, new commits, tracker diff.");
        AnsiConsole.MarkupLine("  A claim with no commit behind it, or a red gate, would not have advanced the run.");
        AnsiConsole.WriteLine();

        if (keep)
        {
            AnsiConsole.MarkupLine($"Kept at [aqua]{Markup.Escape(dir)}[/]. Worth opening:");
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(Path.Combine(".conductor", "logs"))}{Path.DirectorySeparatorChar}session-001.prompt.md[/]  the exact prompt session 1 received");
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(Path.Combine(".conductor", "REPORT.md"))}[/]  the AFK report, as it would land on GitHub");
            AnsiConsole.MarkupLine($"  [grey]TRACKER.md[/]  regenerated from run.db after every session");
            AnsiConsole.MarkupLine($"Inspect it with: [yellow]conductor status -p {Markup.Escape(Path.Combine(dir, "conductor.plan.json"))}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]The demo repo was removed. Re-run with[/] [yellow]--keep[/] [grey]to keep it and read the transcripts.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: [yellow]conductor init[/] in a repo of your own, then [yellow]conductor run --once[/].");
        _ = plan;
    }

    private static Task<ProcResult> GitAsync(string cwd, params string[] args) =>
        ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30));

    /// <summary>Ctrl+C during the demo cancels the run rather than killing the process, so the
    /// throwaway repo still gets cleaned up. The CancelAsync task is discarded on purpose: a
    /// CancelKeyPress handler is void, there is nothing to await it, and the run loop observes the
    /// token on its next check either way.</summary>
    private static void InstallCancelHandler(CancellationTokenSource cts) =>
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = cts.CancelAsync(); };

    /// <summary>git marks objects read-only; a plain recursive delete fails on Windows.</summary>
    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[grey]could not remove {Markup.Escape(dir)} ({Markup.Escape(ex.Message)}) — delete it by hand[/]");
        }
    }

    internal const string Tracker = """
        # Conductor demo — TRACKER

        ## Handoff (overwrite this block each session, <=12 lines, no history)
        last: none. Status: idle.

        ## Checkpoints

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | D1.1 | Write the greeting module | TODO |  |  |
        | D1.2 | Cover it with a test | TODO |  |  |
        | D2.1 | Document the module | TODO |  |  |

        """;

    /// <summary>
    /// The gates are deliberately real subprocesses with real exit codes — that is the mechanism
    /// being demonstrated — and deliberately carry no <c>shell</c>, so they run through the host's
    /// own (powershell on Windows, bash elsewhere). git is already a hard requirement, so it is the
    /// one command guaranteed present on every platform this can run on.
    /// </summary>
    internal static string PlanJson(string dir, string exe)
    {
        var repo = dir.Replace("\\", "/", StringComparison.Ordinal);
        var agent = exe.Replace("\\", "/", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "name": "conductor-demo",
          "repo": "{{repo}}",
          "tracker": "TRACKER.md",
          "agent": {
            "command": "{{agent}}",
            "args": ["fake-agent", "--repo", "{{repo}}", "--session", "{sessionId}", "--prompt", "{prompt}"],
            "provider": "opencode"
          },
          "advisor": { "enabled": false },
          "statusAgent": { "enabled": false },
          "audit": { "enabled": false },
          "stages": [
            { "id": "D1", "title": "Build the thing", "sessions": 2 },
            { "id": "D2", "title": "Write it up", "sessions": 1 }
          ],
          "gates": [
            { "name": "build", "command": "git rev-parse --verify HEAD", "tier": "fast", "timeoutMinutes": 2 },
            { "name": "tests", "command": "git log -1 --format=%H", "tier": "full", "timeoutMinutes": 2 }
          ],
          "limits": {
            "stallMinutes": 2,
            "sessionTimeoutMinutes": 5,
            "stageSlackFactor": 3
          },
          "report": { "commit": false, "push": false }
        }
        """);
    }
}
