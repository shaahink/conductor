using System.Collections.Concurrent;
using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Conductor.Ui;

/// <summary>
/// Full-screen live dashboard. Owns the thread-safe buffers, drains agent/log/gate events on the
/// UI thread, builds an immutable <see cref="DashboardState"/>, and hands it to the pure
/// <see cref="DashboardRenderer"/>. Producers (orchestrator threads) only enqueue; the single UI
/// thread reads — so rendering is glitch-free with no cross-thread mutation of render state.
/// Also hosts scrollable pop-out modals (thinking / output / docs / git / prompt) in the same
/// Live context, so opening one never desyncs the terminal.
/// </summary>
public sealed class LiveDashboard : IProgressSink
{
    private enum Modal { None, Thinking, Output, Docs, Git, Prompt }

    private readonly object _gate = new();
    private readonly List<DashboardState.AgentLine> _agent = new();
    private readonly ReasoningBuffer _thinking = new();
    private readonly List<string> _log = new();
    private readonly ConcurrentQueue<ControlAction> _keys = new();
    private readonly PlanConfig? _plan;
    private DashboardSnapshot _snap = new();
    private IReadOnlyList<GateProgress> _gates = Array.Empty<GateProgress>();
    private int _tick;

    private Modal _modal = Modal.None;
    private string _modalTitle = "";
    private List<string> _modalLines = new();
    private int _modalOffset;

    public LiveDashboard(PlanConfig? plan = null) => _plan = plan;

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
                    ctx.UpdateTarget(BuildTarget());
                    ctx.Refresh();
                    Thread.Sleep(250);
                }
                ctx.UpdateTarget(BuildTarget());
                ctx.Refresh();
            });
    }

    /// <summary>Offline preview: render the seeded state (animated) until a key is pressed.</summary>
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
                    try { if (Console.KeyAvailable) { HandlePreviewKey(Console.ReadKey(intercept: true).Key); if (_quitPreview) break; } }
                    catch (InvalidOperationException) { break; }
                    ctx.UpdateTarget(BuildTarget());
                    ctx.Refresh();
                    Thread.Sleep(120);
                }
            });
    }

    private bool _quitPreview;
    private void HandlePreviewKey(ConsoleKey key)
    {
        if (_modal != Modal.None) { HandleModalKey(key); return; }
        switch (key)
        {
            case ConsoleKey.T: OpenModal(Modal.Thinking); break;
            case ConsoleKey.O: OpenModal(Modal.Output); break;
            case ConsoleKey.D: OpenModal(Modal.Docs); break;
            case ConsoleKey.V: OpenModal(Modal.Git); break;
            case ConsoleKey.X: OpenModal(Modal.Prompt); break;
            default: _quitPreview = true; break;
        }
    }

    private IRenderable BuildTarget()
    {
        if (_modal != Modal.None)
        {
            List<string> lines;
            string title;
            lock (_gate) { lines = _modalLines; title = _modalTitle; }
            return DashboardRenderer.BuildModal(title, lines, _modalOffset, SafeWidth(), SafeHeight());
        }
        return DashboardRenderer.BuildRoot(BuildState());
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
                if (_modal != Modal.None) { HandleModalKey(key); continue; }
                switch (key)
                {
                    case ConsoleKey.T: OpenModal(Modal.Thinking); break;
                    case ConsoleKey.O: OpenModal(Modal.Output); break;
                    case ConsoleKey.D: OpenModal(Modal.Docs); break;
                    case ConsoleKey.V: OpenModal(Modal.Git); break;
                    case ConsoleKey.X: OpenModal(Modal.Prompt); break;
                    case ConsoleKey.P: _keys.Enqueue(ControlAction.PauseAfterSession); break;
                    case ConsoleKey.R: _keys.Enqueue(ControlAction.ResumeRun); break;
                    case ConsoleKey.A: _keys.Enqueue(ControlAction.AbortNow); break;
                    case ConsoleKey.S: _keys.Enqueue(ControlAction.SkipStage); break;
                    case ConsoleKey.K: _keys.Enqueue(ControlAction.KillSession); break;
                    case ConsoleKey.Q: _keys.Enqueue(ControlAction.StopAfterSession); break;
                }
            }
        }
        catch (InvalidOperationException) { /* input redirected */ }
    }

    private void HandleModalKey(ConsoleKey key)
    {
        const int page = 12;
        var max = Math.Max(0, _modalLines.Count - 1);
        switch (key)
        {
            case ConsoleKey.Escape or ConsoleKey.Q: _modal = Modal.None; break;
            case ConsoleKey.UpArrow: _modalOffset = Math.Max(0, _modalOffset - 1); break;
            case ConsoleKey.DownArrow: _modalOffset = Math.Min(max, _modalOffset + 1); break;
            case ConsoleKey.PageUp: _modalOffset = Math.Max(0, _modalOffset - page); break;
            case ConsoleKey.PageDown: _modalOffset = Math.Min(max, _modalOffset + page); break;
            case ConsoleKey.Home: _modalOffset = 0; break;
            case ConsoleKey.End: _modalOffset = max; break;
        }
    }

    private void OpenModal(Modal kind)
    {
        var (title, lines) = kind switch
        {
            Modal.Thinking => ("thinking (full reasoning)", ThinkingLines()),
            Modal.Output => ("agent output (full)", OutputLines()),
            Modal.Docs => ($"docs · stage {_snap.StageId}", DocsLines()),
            Modal.Git => ("git", GitLines()),
            Modal.Prompt => ("compiled prompt (current session)", PromptLines()),
            _ => ("", new List<string>()),
        };
        lock (_gate) { _modal = kind; _modalTitle = title; _modalLines = lines; _modalOffset = Math.Max(0, lines.Count - 1); }
    }

    // ---- modal content providers (captured once on open) ----

    private List<string> ThinkingLines()
    {
        lock (_gate)
            return _thinking.All()
                .SelectMany(e => Split($"{e.Utc.ToLocalTime():HH:mm:ss} ~ {e.Text}"))
                .DefaultIfEmpty("(no thinking captured yet)").ToList();
    }

    private List<string> OutputLines()
    {
        lock (_gate)
            return _agent
                .SelectMany(a => Split($"{a.Utc.ToLocalTime():HH:mm:ss} {Glyph(a.Kind)} {a.Text}"))
                .DefaultIfEmpty("(no agent output yet)").ToList();
    }

    private List<string> DocsLines()
    {
        if (_plan == null) return new() { "(docs unavailable in preview)" };
        var path = Path.Combine(_plan.Repo, _plan.PlanDoc);
        var section = DocsExtractor.ForStageFromFile(path, _snap.StageId);
        if (string.IsNullOrWhiteSpace(section))
            return new() { $"(no section for {_snap.StageId} found in {_plan.PlanDoc})", "", $"doc: {path}" };
        return Split(section);
    }

    private List<string> GitLines()
        => _plan == null ? new() { "(git unavailable in preview)" } : Split(GitView.Summary(_plan.Repo));

    private List<string> PromptLines()
    {
        if (_plan == null) return new() { "(prompt unavailable in preview)" };
        try
        {
            var dir = Path.Combine(_plan.StateDir, "logs");
            var newest = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("session-*.prompt.md").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (newest == null) return new() { "(no compiled prompt yet)" };
            return Split($"# {newest.Name}\n\n" + File.ReadAllText(newest.FullName));
        }
        catch (Exception ex) { return new() { $"(prompt read failed: {ex.Message})" }; }
    }

    private static List<string> Split(string s) => s.Replace("\r\n", "\n").Split('\n').ToList();
    private static string Glyph(string kind) => kind switch { "tool" => "»", "text" => "·", "result" => "◆", "stderr" => "!", "system" => "○", _ => " " };

    private static int SafeWidth() { try { return Math.Max(80, Console.WindowWidth); } catch { return 120; } }
    private static int SafeHeight() { try { return Math.Max(24, Console.WindowHeight); } catch { return 40; } }
}
