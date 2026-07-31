using System.Text;
using Conductor.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

// Last-resort forensic trail: SetExceptionHandler below only reaches the console, which is
// invisible once the Face's Ink alt-screen owns the terminal, and neither path touches the
// Serilog file log. A background-thread throw (fire-and-forget Task.Run, timer callback) has no
// handler anywhere else in the process. Without this, a crash looks identical to a window close —
// silence in conductor.log, no state.json update — and is undiagnosable after the fact.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception, null);
    e.SetObserved();
};

var app = new CommandApp();
app.Configure(c =>
{
    c.SetApplicationName("conductor");
    // SC8.1: `conductor --version` answers the same thing the `version` verb does, because half the
    // world types the flag and a flag that prints "1.0.0" (Spectre's default) would be a lie.
    c.SetApplicationVersion(Conductor.Core.BuildInfo.Current.Full);
    c.AddCommand<RunCommand>("run")
        .WithDescription("Run the plan: engine + control plane + Face TUI, one command. Resumes from saved state; Ctrl+C is safe.");
    c.AddCommand<JourneyCommand>("journey")
        .WithDescription("Pre-flight itinerary: identity, stages, gates, and every human moment — no state written, no agent spawned. Run this before `conductor run [[--paused]]`.");
    c.AddCommand<FaceCommand>("face")
        .WithDescription("Attach a Face TUI to a run that is already going (or --demo for offline synthetic data).");
    c.AddCommand<StatusCommand>("status")
        .WithDescription("Show plan, tracker, and session status.");
    c.AddCommand<GateCommand>("gate")
        .WithDescription("Re-run the gate battery at HEAD (no agent spawned). --full for full battery, default fast-tier only. Clears pendingFix if all green.");
    c.AddCommand<ReportCommand>("report")
        .WithDescription("Regenerate .conductor/REPORT.md from current state.");


    c.AddCommand<PauseCommand>("pause")
        .WithDescription("Ask the running conductor to pause after the current session.");
    c.AddCommand<ResumeCtlCommand>("resume")
        .WithDescription("Resume a paused / needs-attention conductor.");
    c.AddCommand<ApproveCommand>("approve")
        .WithDescription("Approve the owner-gated stage so the conductor advances past it (also R in the TUI).");
    c.AddCommand<KillCommand>("kill")
        .WithDescription("Kill the current agent session (the loop then re-evaluates).");
    c.AddCommand<SkipCommand>("skip")
        .WithDescription("Skip the current stage and flag it for human review.");
    c.AddCommand<InjectCommand>("inject")
        .WithDescription("Queue an instruction for the agent's next session (also available via the I key in the dashboard).");
    c.AddCommand<AbortCommand>("abort")
        .WithDescription("Kill the session and stop the conductor.");
    c.AddCommand<RetryStageCommand>("retry-stage")
        .WithDescription("Reset attempt counter and re-queue a deliver session for the current stage.");
    c.AddCommand<RollbackCommand>("rollback")
        .WithDescription("Reset the working tree to the stage start commit (refuses if dirty, use --yes).");
    c.AddCommand<PauseAfterStageCommand>("pause-after-stage")
        .WithDescription("Park at Paused after the current stage completes.");
    c.AddCommand<GotoCommand>("goto")
        .WithDescription("Jump to a different stage (clears pending state for the old stage).");
    c.AddCommand<RolloverCommand>("rollover")
        .WithDescription("Set/clear the session-token rollover for THIS run only: rollover <tokens|off|clear>. Run-state only — never writes the plan.");
    c.AddCommand<HeartbeatCommand>("heartbeat")
        .WithDescription("Ask the running conductor to refresh .conductor/REPORT.md immediately (only meaningful during a live session; also in the : command palette).");
    c.AddCommand<PlanCommand>("plan")
        .WithDescription("Plan management: set a field, reload+validate, or add a stage. Sub-commands: set <key> <value>, reload, add-stage <json>.");
    c.AddCommand<TasksCommand>("tasks")
        .WithDescription("Show task graph: sub-tasks per checkpoint from the event log.");
    c.AddCommand<TaskCommand>("task")
        .WithDescription("Checkpoint CRUD from run.db: --list, --done, --in-progress.");
    c.AddCommand<NoteCommand>("note")
        .WithDescription("Write a note/finding to the knowledge ledger (run.db ledger table).");
    c.AddCommand<BugCommand>("bug")
        .WithDescription("Tracked bugs that outlive the session that found them. Sub-commands: new <title>, list [[--all]], fix <id>.");
    c.AddCommand<LogCommand>("log")
        .WithDescription("Query the structured JSON log. Filter by stage, gate, outcome, etc. Example: conductor log --query \"stage=P7 and gate=build and outcome=fail\"");
    c.AddCommand<DemoCommand>("demo")
        .WithDescription("Drive a complete plan end to end against a built-in fake agent, in a throwaway repo. No credentials, no spend, every platform.");
    // Hidden: the agent `demo` spawns. An implementation detail, not a verb to reach for.
    c.AddCommand<FakeAgentCommand>("fake-agent").IsHidden();
    c.AddCommand<NewPlanCommand>("new-plan")
        .WithDescription("Scaffold a new plan + TRACKER.md.");
    c.AddCommand<InitCommand>("init")
        .WithDescription("Scaffold a runnable plan + editable templates + TRACKER, with gates chosen from the detected repo type (dotnet/node/go/rust/python).");
    c.AddCommand<DoctorCommand>("doctor")
        .WithDescription("<2s health check: agent CLI, git, face-go binary, DNS/disk/API reachability, budget headroom, Telegram — says exactly what is missing.");
    c.AddCommand<AuditCommand>("audit")
        .WithDescription("Post-hoc audit replay: run an audit prompt against a completed stage (read-only diagnostic). Requires --replay flag. Output written to .conductor/audits/.");
    c.AddCommand<McpServeCommand>("mcp-serve")
        .WithDescription("Run the MCP task server (JSON-RPC 2.0 over stdio) for agent task management.");
    c.AddCommand<CompletionCommand>("completion")
        .WithDescription("Generate shell completion scripts (powershell or bash).");
    c.AddCommand<BgCommand>("bg")
        .WithDescription("Background process management: start|status|logs|stop.");
    c.AddCommand<VersionCommand>("version")
        .WithDescription("What this binary is: semver, git sha and build date stamped at build, plus which file answered. --json for machines, --short for scripts.");
    c.AddCommand<UpdateCommand>("update")
        .WithDescription("Check the latest release and swap this binary for it. Refuses while a run is live. --check to look without installing.");
    c.AddCommand<ChatCommand>("chat")
        .WithDescription("F8.1: Ask questions about a running conductor plan. The agent has MCP access to run.db, the ledger, and control verbs. Example: conductor chat \"how did session 9 die?\"");
    c.SetExceptionHandler((ex, _) =>
    {
        // A parse/usage error is a typo, not a crash — no forensic dump, or every mistyped flag
        // leaves a .conductor/logs/crash-*.log in whatever directory it was typed in.
        if (ex is not (CommandParseException or CommandRuntimeException))
            WriteCrashLog("Spectre.SetExceptionHandler", ex, null);
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex is InvalidOperationException or FileNotFoundException or CommandParseException or CommandRuntimeException ? ex.Message : ex.ToString())}");
        return 1;
    });
});
return await app.RunAsync(args).ConfigureAwait(false);

// Deliberately independent of the DI-built Serilog logger (not constructed yet at this point, and
// this must survive even if that construction is what's failing). Best-effort: a crash-logging
// path that can itself throw would defeat the purpose.
#pragma warning disable MA0045 // sync I/O by design — an exception-handler callback must not hand off async work that could race process teardown
static void WriteCrashLog(string source, Exception? ex, string? raw)
{
    try
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), ".conductor", "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log");
        File.WriteAllText(path, $"{DateTime.UtcNow:O} UTC — {source}{Environment.NewLine}{ex?.ToString() ?? raw ?? "(no exception object)"}{Environment.NewLine}");
    }
    catch { /* forensic dump is best-effort only */ }
}
#pragma warning restore MA0045
