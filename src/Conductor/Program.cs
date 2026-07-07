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
        .WithDescription("Run the plan loop (resumes from saved state). Ctrl+C is safe — state persists.");
    c.AddCommand<StatusCommand>("status")
        .WithDescription("Show plan, tracker, and session status.");
    c.AddCommand<ReportCommand>("report")
        .WithDescription("Regenerate .conductor/REPORT.md from current state.");
    c.AddCommand<PreviewCommand>("preview")
        .WithDescription("Render the dashboard offline from current state (+ synthetic session data) to verify the UI. Press any key to exit.");
    c.AddCommand<PauseCommand>("pause")
        .WithDescription("Ask the running conductor to pause after the current session.");
    c.AddCommand<ResumeCtlCommand>("resume")
        .WithDescription("Resume a paused / needs-attention conductor.");
    c.AddCommand<KillCommand>("kill")
        .WithDescription("Kill the current agent session (the loop then re-evaluates).");
    c.AddCommand<SkipCommand>("skip")
        .WithDescription("Skip the current stage and flag it for human review.");
    c.AddCommand<InjectCommand>("inject")
        .WithDescription("Queue an instruction for the agent's next session (also available via the I key in the dashboard).");
    c.AddCommand<AbortCommand>("abort")
        .WithDescription("Kill the session and stop the conductor.");
    c.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex is InvalidOperationException or FileNotFoundException ? ex.Message : ex.ToString())}");
        return 1;
    });
});
return app.Run(args);
