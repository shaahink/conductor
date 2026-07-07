using System.Collections.Concurrent;
using Conductor.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Conductor.Ui;

/// <summary>Full-screen live dashboard for when you're behind the laptop.</summary>
public sealed class LiveDashboard : IProgressSink
{
    private readonly object _gate = new();
    private readonly List<string> _tail = new();
    private readonly List<string> _log = new();
    private readonly ConcurrentQueue<ControlAction> _keys = new();
    private DashboardSnapshot _snap = new();

    public void Log(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > 300) _log.RemoveRange(0, 100);
        }
    }

    public void AgentEvent(AgentEvent ev)
    {
        var glyph = ev.Kind switch
        {
            "tool" => "»",
            "text" => "·",
            "result" => "◆",
            "stderr" => "!",
            "system" => "○",
            _ => " ",
        };
        lock (_gate)
        {
            _tail.Add($"{ev.Utc:HH:mm:ss} {glyph} {ev.Text}");
            if (_tail.Count > 300) _tail.RemoveRange(0, 100);
        }
    }

    public void Snapshot(DashboardSnapshot snap) { lock (_gate) _snap = snap; }

    public ControlAction? PollControl() => _keys.TryDequeue(out var a) ? a : null;

    /// <summary>Runs on the main thread until the orchestrator task completes.</summary>
    public void RunUiLoop(Task orchestrator)
    {
        var layout = new Layout("root").SplitRows(
            new Layout("header").Size(6),
            new Layout("body").SplitColumns(
                new Layout("left").Ratio(2),
                new Layout("right").Ratio(3)),
            new Layout("footer").Size(9));

        AnsiConsole.Live(layout).Start(ctx =>
        {
            while (!orchestrator.IsCompleted)
            {
                PollKeys();
                Render(layout);
                ctx.Refresh();
                Thread.Sleep(300);
            }
            Render(layout);
            ctx.Refresh();
        });
    }

    private void PollKeys()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                ControlAction? a = key switch
                {
                    ConsoleKey.P => ControlAction.PauseAfterSession,
                    ConsoleKey.R => ControlAction.ResumeRun,
                    ConsoleKey.A => ControlAction.AbortNow,
                    ConsoleKey.S => ControlAction.SkipStage,
                    ConsoleKey.K => ControlAction.KillSession,
                    ConsoleKey.Q => ControlAction.StopAfterSession,
                    _ => null,
                };
                if (a != null) _keys.Enqueue(a.Value);
            }
        }
        catch (InvalidOperationException) { /* input redirected */ }
    }

    private void Render(Layout layout)
    {
        DashboardSnapshot s;
        List<string> tail, log;
        lock (_gate)
        {
            s = _snap;
            tail = _tail.TakeLast(18).ToList();
            log = _log.TakeLast(6).ToList();
        }

        var statusColor = s.Status switch
        {
            "Running" => "green",
            "VerifyingGates" => "yellow",
            "Backoff" => "orange1",
            "Paused" => "grey",
            "NeedsHuman" => "red",
            "Completed" => "aqua",
            "Aborted" => "red",
            _ => "silver",
        };
        var header = new Rows(
            new Markup($"[bold aqua]Conductor[/] — [bold]{Esc(s.PlanName)}[/]   [{statusColor}]● {Esc(s.Status)}[/]" +
                       (s.AttentionReason != null ? $"  [red]{Esc(s.AttentionReason)}[/]" : "")),
            new Markup($"stage [bold]{Esc(s.StageId)}[/] {Esc(s.StageTitle)} · session #{s.SessionNumber} {Esc(s.SessionKind)}" +
                       (s.Attempt > 0 ? $" · attempt {s.Attempt}/{s.MaxAttempts}" : "")),
            new Markup($"checkpoints [bold]{s.DoneCount}/{s.TotalCount}[/] · cost ${s.TotalCostUsd:0.00}" +
                       (s.SessionElapsed > TimeSpan.Zero ? $" · elapsed {s.SessionElapsed:hh\\:mm\\:ss} · last output {s.LastActivityAgoSec:0}s ago" : "") +
                       (s.BackoffUntilUtc != null ? $" · [orange1]backoff until {s.BackoffUntilUtc:HH:mm} UTC[/]" : "")));
        layout["header"].Update(new Panel(header).Expand());

        var stages = new Table().Border(TableBorder.Rounded).Expand();
        stages.AddColumn("Stage");
        stages.AddColumn("Done");
        stages.AddColumn("State");
        foreach (var (id, doneN, total, st) in s.StageOverview)
        {
            var mark = st switch
            {
                "done" => "[green]done[/]",
                "active" => "[bold yellow]← active[/]",
                "skipped" => "[red]skipped[/]",
                _ => "[grey]todo[/]",
            };
            stages.AddRow(Esc(id), $"{doneN}/{total}", mark);
        }
        var current = new Table().Border(TableBorder.Rounded).Expand();
        current.AddColumn("Checkpoint");
        current.AddColumn("Status");
        foreach (var (id, st) in s.StageCheckpoints)
        {
            var color = st.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) ? "green"
                : st.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) ? "red"
                : st.StartsWith("IN", StringComparison.OrdinalIgnoreCase) ? "yellow" : "grey";
            current.AddRow(Esc(id), $"[{color}]{Esc(st)}[/]");
        }
        layout["left"].Update(new Rows(
            new Panel(stages).Header("plan"),
            new Panel(current).Header($"stage {Esc(s.StageId)}")));

        IRenderable tailBody = tail.Count > 0
            ? new Rows(tail.Select(t => (IRenderable)new Markup("[silver]" + Esc(t) + "[/]")).ToArray())
            : new Markup("[grey](no agent output yet)[/]");
        layout["right"].Update(new Panel(tailBody).Header("agent").Expand());

        var footerRows = new List<IRenderable>();
        if (!string.IsNullOrEmpty(s.GateSummary))
            footerRows.Add(new Markup("[bold]gates:[/] " + Esc(s.GateSummary)));
        footerRows.AddRange(log.Select(l => (IRenderable)new Markup("[grey]" + Esc(l) + "[/]")));
        footerRows.Add(new Markup("[grey][[P]]ause  [[R]]esume  [[K]]ill session  [[S]]kip stage  [[Q]]uit after session  [[A]]bort now[/]"));
        layout["footer"].Update(new Panel(new Rows(footerRows)).Header("conductor").Expand());
    }

    private static string Esc(string s) => Markup.Escape(s);
}
