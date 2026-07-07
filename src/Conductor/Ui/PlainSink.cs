using Conductor.Core;

namespace Conductor.Ui;

/// <summary>Line-based output for --no-dashboard, redirected output, or CI.</summary>
public sealed class PlainSink : IProgressSink
{
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private readonly bool _interactive = !Console.IsInputRedirected;

    public void Log(string line) => Console.WriteLine(line);

    public void AgentEvent(AgentEvent ev)
    {
        if (ev.Kind is "tool" or "text" or "result" or "stderr")
            Console.WriteLine($"[{ev.Utc:HH:mm:ss}]   {ev.Kind,-6} {ev.Text}");
    }

    public void Snapshot(DashboardSnapshot snap)
    {
        if (DateTime.UtcNow - _lastHeartbeat < TimeSpan.FromSeconds(60)) return;
        _lastHeartbeat = DateTime.UtcNow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ── {snap.Status} · stage {snap.StageId} · {snap.DoneCount}/{snap.TotalCount} done · ${snap.TotalCostUsd:0.00}" +
                          (snap.SessionElapsed > TimeSpan.Zero ? $" · session {snap.SessionElapsed:hh\\:mm\\:ss}, last output {snap.LastActivityAgoSec:0}s ago" : ""));
    }

    public ControlAction? PollControl()
    {
        if (!_interactive) return null;
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.P: return ControlAction.PauseAfterSession;
                    case ConsoleKey.R: return ControlAction.ResumeRun;
                    case ConsoleKey.A: return ControlAction.AbortNow;
                    case ConsoleKey.S: return ControlAction.SkipStage;
                    case ConsoleKey.K: return ControlAction.KillSession;
                    case ConsoleKey.Q: return ControlAction.StopAfterSession;
                }
            }
        }
        catch (InvalidOperationException) { }
        return null;
    }
}
