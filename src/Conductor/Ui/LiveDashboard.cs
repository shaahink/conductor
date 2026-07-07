using System.Collections.Concurrent;
using Conductor.Core;
using Spectre.Console;

namespace Conductor.Ui;

/// <summary>
/// Full-screen live dashboard. Owns the thread-safe buffers, drains agent/log/gate events on the
/// UI thread, builds an immutable <see cref="DashboardState"/>, and hands it to the pure
/// <see cref="DashboardRenderer"/>. Producers (orchestrator threads) only enqueue; the single UI
/// thread reads — so rendering is glitch-free with no cross-thread mutation of render state.
/// </summary>
public sealed class LiveDashboard : IProgressSink
{
    private readonly object _gate = new();
    private readonly List<DashboardState.AgentLine> _agent = new();
    private readonly ReasoningBuffer _thinking = new();
    private readonly List<string> _log = new();
    private readonly ConcurrentQueue<ControlAction> _keys = new();
    private DashboardSnapshot _snap = new();
    private IReadOnlyList<GateProgress> _gates = Array.Empty<GateProgress>();
    private int _tick;

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
                _thinking.Add(ev.Text, ev.Utc);
            }
            else
            {
                _agent.Add(new DashboardState.AgentLine(ev.Kind, ev.Text, ev.Utc));
                if (_agent.Count > 500) _agent.RemoveRange(0, 100);
            }
        }
    }

    public void Snapshot(DashboardSnapshot snap) { lock (_gate) _snap = snap; }

    public void GateProgress(IReadOnlyList<GateProgress> gates) { lock (_gate) _gates = gates; }

    public ControlAction? PollControl() => _keys.TryDequeue(out var a) ? a : null;

    /// <summary>Runs on the main thread until the orchestrator task completes.</summary>
    public void RunUiLoop(Task orchestrator)
    {
        AnsiConsole.Live(new Text(""))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Bottom)
            .Start(ctx =>
            {
                while (!orchestrator.IsCompleted)
                {
                    PollKeys();
                    ctx.UpdateTarget(DashboardRenderer.BuildRoot(BuildState()));
                    ctx.Refresh();
                    Thread.Sleep(250);
                }
                ctx.UpdateTarget(DashboardRenderer.BuildRoot(BuildState()));
                ctx.Refresh();
            });
    }

    private DashboardState BuildState()
    {
        lock (_gate)
        {
            var snap = _gates.Count > 0 ? _snap with { Gates = _gates } : _snap;
            return new DashboardState
            {
                Snap = snap,
                Agent = _agent.Skip(Math.Max(0, _agent.Count - 15)).ToArray(),
                Thinking = _thinking.Recent(10).Select(e => new DashboardState.ThinkingLine(e.Utc, e.Text)).ToArray(),
                Log = _log.Skip(Math.Max(0, _log.Count - 5)).ToArray(),
                Width = SafeWidth(),
                Height = SafeHeight(),
                Tick = _tick++,
            };
        }
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

    private static int SafeWidth() { try { return Math.Max(80, Console.WindowWidth); } catch { return 120; } }
    private static int SafeHeight() { try { return Math.Max(24, Console.WindowHeight); } catch { return 40; } }

    /// <summary>Offline preview: render the seeded state (animated spinner) until a key is pressed.
    /// No orchestration, no writes — used by `conductor preview` to verify the UI without a run.</summary>
    public void RunPreview()
    {
        AnsiConsole.Live(new Text(""))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Bottom)
            .Start(ctx =>
            {
                while (true)
                {
                    try { if (Console.KeyAvailable) { Console.ReadKey(intercept: true); break; } }
                    catch (InvalidOperationException) { break; }
                    ctx.UpdateTarget(DashboardRenderer.BuildRoot(BuildState()));
                    ctx.Refresh();
                    Thread.Sleep(120);
                }
            });
    }
}
