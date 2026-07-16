namespace Conductor.Core;

/// <summary>Line-based output for --no-dashboard, redirected output, or CI. While the Face TUI owns
/// the terminal the sink is muted (see <see cref="Mute"/>): a second console writer shifts the Face's
/// alt-screen repaints into garbage, and a competing <c>Console.ReadKey</c> steals its keystrokes.</summary>
public sealed class PlainSink : IProgressSink
{
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private readonly bool _interactive = !Console.IsInputRedirected;
    private ControlAction? _pendingConfirm;
    private volatile bool _muted;

    /// <summary>Silence console output and key polling while another process (the Face) owns the
    /// terminal. Narration is not lost: it still reaches .conductor/conductor.log, the structured
    /// Serilog files, and the event store the Face renders from.</summary>
    public void Mute() => _muted = true;

    /// <summary>Resume console output — the Face exited (or died), so this terminal is ours again.</summary>
    public void Unmute() => _muted = false;

    public void Log(string line)
    {
        if (_muted) return;
        Console.WriteLine(line);
    }

    public void AgentEvent(AgentEvent ev)
    {
        if (_muted) return;
        if (ev.Kind is "tool" or "text" or "result" or "stderr")
            Console.WriteLine($"[{ev.Utc.ToLocalTime():HH:mm:ss}]   {ev.Kind,-6} {ev.Text}");
    }

    public void Snapshot(DashboardSnapshot snap)
    {
        if (_muted) return;
        if (DateTime.UtcNow - _lastHeartbeat < TimeSpan.FromSeconds(60)) return;
        _lastHeartbeat = DateTime.UtcNow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ── {snap.Status} · stage {snap.StageId} · {snap.DoneCount}/{snap.TotalCount} done · ${snap.TotalCostUsd:0.00}" +
                          (snap.SessionElapsed > TimeSpan.Zero ? $" · session {snap.SessionElapsed:hh\\:mm\\:ss}, last output {snap.LastActivityAgoSec:0}s ago" : ""));
    }

    public ControlCommand? PollControl()
    {
        if (_muted || !_interactive) return null;
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.P: _pendingConfirm = null; return ControlCommand.Of(ControlAction.PauseAfterSession);
                    case ConsoleKey.R: _pendingConfirm = null; return ControlCommand.Of(ControlAction.ResumeRun);
                    case ConsoleKey.A:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.AbortNow, ref _pendingConfirm);
                          if (act != null) return ControlCommand.Of(act.Value);
                          Console.WriteLine("[CONFIRM] Press A again to confirm ABORT (any other key cancels)");
                          break; }
                    case ConsoleKey.S:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.SkipStage, ref _pendingConfirm);
                          if (act != null) return ControlCommand.Of(act.Value);
                          Console.WriteLine("[CONFIRM] Press S again to confirm SKIP (any other key cancels)");
                          break; }
                    case ConsoleKey.K:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.KillSession, ref _pendingConfirm);
                          if (act != null) return ControlCommand.Of(act.Value);
                          Console.WriteLine("[CONFIRM] Press K again to confirm KILL (any other key cancels)");
                          break; }
                    case ConsoleKey.Q: _pendingConfirm = null; return ControlCommand.Of(ControlAction.StopAfterSession);
                    default: _pendingConfirm = null; break;
                }
            }
        }
        catch (InvalidOperationException) { }
        return null;
    }
}
