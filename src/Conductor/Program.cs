using System.Text;
using Conductor.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

var app = new CommandApp();
app.Configure(c =>
{
    c.SetApplicationName("conductor");
    c.AddCommand<RunCommand>("run")
        .WithDescription("Run the plan: engine + control plane + Face TUI, one command. Resumes from saved state; Ctrl+C is safe.");
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
    c.AddCommand<PlanCommand>("plan")
        .WithDescription("Plan management: set a field, reload+validate, or add a stage. Sub-commands: set <key> <value>, reload, add-stage <json>.");
    c.AddCommand<TasksCommand>("tasks")
        .WithDescription("Show task graph: sub-tasks per checkpoint from the event log.");
    c.AddCommand<TaskCommand>("task")
        .WithDescription("Checkpoint CRUD from run.db: --list, --done, --in-progress.");
    c.AddCommand<NoteCommand>("note")
        .WithDescription("Write a note/finding to the knowledge ledger (run.db ledger table).");
    c.AddCommand<LogCommand>("log")
        .WithDescription("Query the structured JSON log. Filter by stage, gate, outcome, etc. Example: conductor log --query \"stage=P7 and gate=build and outcome=fail\"");
    c.AddCommand<NewPlanCommand>("new-plan")
        .WithDescription("Scaffold a new plan + TRACKER.md.");
    c.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Print exactly what will happen on resume: pending sessions, gates, owner-approval, remaining stages.");
    c.AddCommand<AuditCommand>("audit")
        .WithDescription("Post-hoc audit replay: run an audit prompt against a completed stage (read-only diagnostic). Requires --replay flag. Output written to .conductor/audits/.");
    c.AddCommand<McpServeCommand>("mcp-serve")
        .WithDescription("Run the MCP task server (JSON-RPC 2.0 over stdio) for agent task management.");
    c.AddCommand<CompletionCommand>("completion")
        .WithDescription("Generate shell completion scripts (powershell or bash).");
    c.AddCommand<BgCommand>("bg")
        .WithDescription("Background process management: start|status|logs|stop.");
    c.AddCommand<ChatCommand>("chat")
        .WithDescription("F8.1: Ask questions about a running conductor plan. The agent has MCP access to run.db, the ledger, and control verbs. Example: conductor chat \"how did session 9 die?\"");
    c.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex is InvalidOperationException or FileNotFoundException ? ex.Message : ex.ToString())}");
        return 1;
    });
});
return await app.RunAsync(args).ConfigureAwait(false);
