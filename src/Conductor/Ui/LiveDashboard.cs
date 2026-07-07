using System.Collections.Concurrent;
using Conductor.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Conductor.Ui;

/// <summary>Full-screen live dashboard for when you're behind the laptop.</summary>
public sealed class LiveDashboard : IProgressSink
{
    private readonly object _gate = new();
    private readonly List<(string Kind, string Text, DateTime Utc)> _tail = new();
    private readonly List<(string Text, DateTime Utc)> _thinking = new();
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
        lock (_gate)
        {
            if (ev.Kind == "thinking")
            {
                _thinking.Add((ev.Text, ev.Utc));
                if (_thinking.Count > 300) _thinking.RemoveRange(0, 100);
            }
            else
            {
                _tail.Add((ev.Kind, ev.Text, ev.Utc));
                if (_tail.Count > 400) _tail.RemoveRange(0, 100);
            }
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
                new Layout("right").SplitRows(
                    new Layout("agent").Ratio(3),
                    new Layout("thinking").Ratio(2))),
            new Layout("footer").Size(9));

        AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
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
        List<(string Kind, string Text, DateTime Utc)> tail;
        List<(string Text, DateTime Utc)> thinking;
        List<string> log;
        lock (_gate)
        {
            s = _snap;
            tail = _tail.TakeLast(15).ToList();
            thinking = _thinking.TakeLast(8).ToList();
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
        var tokens = s.TokensInput + s.TokensOutput > 0
            ? $" · tokens {Human(s.TokensInput)}in/{Human(s.TokensOutput)}out" + (s.TokensReasoning > 0 ? $"/{Human(s.TokensReasoning)}think" : "")
            : "";
        var header = new Rows(
            new Markup($"[bold aqua]Conductor[/] — [bold]{Esc(s.PlanName)}[/]   [{statusColor}]● {Esc(s.Status)}[/]" +
                       (s.AttentionReason != null ? $"  [red]{Esc(s.AttentionReason)}[/]" : "")),
            new Markup($"stage [bold]{Esc(s.StageId)}[/] {Esc(s.StageTitle)} · session #{s.SessionNumber} {Esc(s.SessionKind)}" +
                       (s.Attempt > 0 ? $" · attempt {s.Attempt}/{s.MaxAttempts}" : "") +
                       (s.ResumeCount > 0 ? $" · resume {s.ResumeCount}" : "") +
                       (!string.IsNullOrEmpty(s.CurrentCheckpoint) ? $" · [aqua]▸ {Esc(s.CurrentCheckpoint)}[/]" : "")),
            new Markup($"checkpoints [bold]{s.DoneCount}/{s.TotalCount}[/] · cost [bold]${s.TotalCostUsd:0.0000}[/]" +
                       (s.SessionCostUsd > 0 ? $" (session ${s.SessionCostUsd:0.0000})" : "") + tokens +
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

        // agent activity (text/tool/result) — auto-tailed
        IRenderable agentBody = tail.Count > 0
            ? new Rows(tail.Select(t => (IRenderable)new Markup(Clip(t.Kind, t.Text, t.Utc))).ToArray())
            : new Markup("[grey](no agent output yet)[/]");
        layout["agent"].Update(new Panel(agentBody).Header("agent").Expand());

        // thinking lane — dim, auto-tailed
        IRenderable thinkBody = thinking.Count > 0
            ? new Rows(thinking.Select(t => (IRenderable)new Markup($"[grey37]{t.Utc:HH:mm:ss} ~ {Esc(t.Text)}[/]")).ToArray())
            : new Markup("[grey](no thinking captured — needs --thinking + --format json)[/]");
        layout["thinking"].Update(new Panel(thinkBody).Header("thinking").Expand());

        var footerRows = new List<IRenderable>();
        if (!string.IsNullOrEmpty(s.GateSummary))
            footerRows.Add(new Markup("[bold]gates:[/] " + Esc(s.GateSummary)));
        footerRows.AddRange(log.Select(l => (IRenderable)new Markup("[grey]" + Esc(l) + "[/]")));
        footerRows.Add(new Markup("[grey][[P]]ause  [[R]]esume  [[K]]ill session  [[S]]kip stage  [[Q]]uit after session  [[A]]bort now[/]"));
        layout["footer"].Update(new Panel(new Rows(footerRows)).Header("conductor").Expand());
    }

    private static string Clip(string kind, string text, DateTime utc)
    {
        var (glyph, color) = kind switch
        {
            "tool" => ("»", "deepskyblue1"),
            "text" => ("·", "silver"),
            "result" => ("◆", "aqua"),
            "stderr" => ("!", "orange1"),
            "system" => ("○", "grey"),
            _ => (" ", "grey"),
        };
        return $"[{color}]{utc:HH:mm:ss} {glyph} {Esc(text)}[/]";
    }

    private static string Human(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1000 ? $"{n / 1000.0:0.0}k" : n.ToString();

    private static string Esc(string s) => Markup.Escape(s);
}
