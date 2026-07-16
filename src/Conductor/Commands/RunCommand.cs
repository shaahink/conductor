using System.ComponentModel;
using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Face;
using Conductor.Core.Hosting;
using Conductor.Core.Http;
using Conductor.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--dry-run")]
        [Description("Print the next session's prompt and exit — nothing is spawned.")]
        public bool DryRun { get; init; }

        [CommandOption("--once")]
        [Description("Run exactly one session, then stop.")]
        public bool Once { get; init; }

        [CommandOption("--max-sessions <N>")]
        [Description("Stop after N sessions this run (0 = unlimited).")]
        public int MaxSessions { get; init; }

        [CommandOption("--headless")]
        [Description("No TUI: plain line output in this terminal. The control plane still runs, so a Face can attach later.")]
        public bool Headless { get; init; }

        [CommandOption("--no-face")]
        [Description("Run the control plane but do not spawn the Face TUI (attach your own: `conductor face`).")]
        public bool NoFace { get; init; }

        [CommandOption("--no-control-plane")]
        [Description("Disable the localhost HTTP+SSE control plane entirely. Implies --headless (the Face needs it).")]
        public bool NoControlPlane { get; init; }

        [CommandOption("--port <PORT>")]
        [Description("Preferred control-plane port (default 4317). If taken, the next free port is used — concurrent runs never collide.")]
        public int ControlPlanePort { get; init; } = 4317;

        [CommandOption("--paused")]
        [Description("Start idle: dashboard + control plane come up but no session spawns until you resume (author the plan / seed the board first).")]
        public bool Paused { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var planPathArg = settings.ResolvePlanPath();
        var plan = PlanConfig.Load(planPathArg);
        Directory.CreateDirectory(plan.StateDir);
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        // state.json is the pre-M2 legacy carrier — the live store is run.db's run_state table.
        // When the file yields nothing, resume from the store, or `conductor run` silently starts a
        // fresh run #1 every time and "run again to resume" is a lie (2026-07-17 dogfood).
        if (string.IsNullOrEmpty(state.RunId) && state.SessionCounter == 0)
        {
            var resumed = Core.Store.RunStateResume.TryLoadLatest(Path.Combine(plan.StateDir, "run.db"), plan.Name);
            if (resumed != null)
            {
                state = resumed;
                AnsiConsole.MarkupLine(
                    $"[grey]resuming run {Markup.Escape(Short(state.RunId))} — {state.SessionCounter} session(s) so far, status {state.Status}[/]");
            }
        }
        if (string.IsNullOrEmpty(state.RunId)) state.RunId = Guid.NewGuid().ToString("N");

        // `conductor run` is ONE command: engine + control plane + Face TUI, one process tree. The plain
        // (headless) path exists for CI, dry runs and redirected output — it is no longer the way you get
        // a UI, and there is no second terminal to start.
        var controlPlane = !settings.NoControlPlane && !settings.DryRun;
        var wantFace = controlPlane
                       && !settings.Headless
                       && !settings.NoFace
                       && !settings.DryRun
                       && !Console.IsOutputRedirected;

        // M2: the store (SqliteRunStore) is created inside ConductorHost.Build — it owns
        // the IEventSink (events table) and IRunStore (all writes).
        var opts = new RunOptions(settings.DryRun, settings.Once, settings.MaxSessions, controlPlane, settings.ControlPlanePort, settings.Paused);
        using var cts = new CancellationTokenSource();
#pragma warning disable MA0045 // CancelAsync doesn't exist on CancellationTokenSource
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
#pragma warning restore MA0045

        var startedUtc = DateTime.UtcNow;
        int exitCode;
        FaceLauncher.FaceHandle? face = null;
        try
        {
            // When the Face owns the terminal, the engine's console sink must stay off or the two
            // interleave and corrupt the render. Everything still goes to .conductor/logs/.
            var sink = new PlainSink();
            using var host = ConductorHost.Build(plan, state, sink, opts, consoleSink: !wantFace);

            var server = host.Services.GetService<ControlPlaneServer>();
            var bound = server?.Start() == true; // never fatal: a bind failure just means no clients

            if (wantFace && bound)
            {
                face = FaceLauncher.Start(
                    $"http://127.0.0.1:{server!.Port}",
                    host.Services.GetRequiredService<ILogger<RunCommand>>(),
                    host.Services.GetService<ProcessSupervisor>(),
                    server.Token);
                if (face is not null)
                {
                    // The Face inherits this console: the sink must go quiet or its lines land in the
                    // Face's alt-screen and shift every repaint (and PollControl steals its keys).
                    // If the Face dies or the user quits it, the run continues — unmuted, headless.
                    sink.Mute();
                    face.Process.EnableRaisingEvents = true;
                    face.Process.Exited += (_, _) => sink.Unmute();
                    if (face.Process.HasExited) sink.Unmute();
                }
            }

#pragma warning disable MA0045 // sync-over-async boundary: Spectre.Cli Execute must return int
            exitCode = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token).GetAwaiter().GetResult();
#pragma warning restore MA0045
        }
        finally
        {
            face?.Dispose();
        }

        // The Face owned the terminal (sink muted) — without this epilogue an early exit is a silent
        // flash and the owner is left asking "what just happened?" (2026-07-17 dogfood: a stale abort
        // ended the run in <1s with nothing on screen). Printed after the Face is gone, always.
        PrintEpilogue(exitCode, plan, state, planPathArg, startedUtc);
        return exitCode;
    }

    private static void PrintEpilogue(int code, PlanConfig plan, RunState state, string planPathArg, DateTime startedUtc)
    {
        var meaning = code switch
        {
            0 => "stopped cleanly",
            2 => "aborted",
            4 => "another conductor already holds this plan's lock",
            130 => "cancelled (Ctrl+C) — safe, nothing lost",
            _ => $"exit code {code}",
        };
        AnsiConsole.MarkupLine($"[bold]run ended[/] — {Markup.Escape(state.Status.ToString())} · {Markup.Escape(meaning)}" +
            (string.IsNullOrEmpty(state.AttentionReason) ? "" : $" · [yellow]{Markup.Escape(state.AttentionReason)}[/]"));

        // A crash dump written during this run is the first thing to read — say so explicitly.
        foreach (var dir in new[] { Path.Combine(plan.StateDir, "logs"), Path.Combine(Directory.GetCurrentDirectory(), ".conductor", "logs") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var crash in Directory.GetFiles(dir, "crash-*.log").Where(f => File.GetLastWriteTimeUtc(f) >= startedUtc))
                AnsiConsole.MarkupLine($"[red]crash dump:[/] {Markup.Escape(crash)}");
        }

        AnsiConsole.MarkupLine($"[grey]history: {Markup.Escape(Path.Combine(plan.StateDir, "conductor.log"))} · run {Markup.Escape(Short(state.RunId))}, {state.SessionCounter} session(s)[/]");
        if (code != 4)
            AnsiConsole.MarkupLine($"resume: [yellow]conductor run -p {Markup.Escape(planPathArg)}[/]");
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "?" : id.Length >= 8 ? id[..8] : id;
}
