using System.Collections.Concurrent;
using System.Text;
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
    private enum Modal { None, Thinking, Output, Docs, Git, Prompt, Status }

    private readonly Lock _gate = new();
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

    private bool _inputActive;
    private readonly StringBuilder _inputBuffer = new();

    private ControlAction? _pendingConfirm;

    private volatile bool _statusRunning;
    private List<string> _statusLines = new();

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
        // Alt-screen buffer: the dashboard owns a scratch screen and the user's scrollback + prompt
        // are restored on every exit path (normal, exception, Ctrl+C). Redirected output → inert.
        using var alt = AltScreen.Enter();
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
        // Redirected output (CI / piped) can't host a Live region — render one static frame instead.
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            AnsiConsole.Write(BuildTarget());
            return;
        }
        using var alt = AltScreen.Enter();
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
            case ConsoleKey.G when _plan?.StatusAgent is { Enabled: true }: StartStatusAgent(); break;
            default: _quitPreview = true; break;
        }
    }

    private IRenderable BuildTarget()
    {
        if (_inputActive) return DashboardRenderer.BuildInput(_inputBuffer.ToString(), SafeWidth(), SafeHeight());
        if (_modal == Modal.Status)
        {
            List<string> lines;
            lock (_gate) lines = _statusRunning
                ? new List<string> { "status agent is analysing the run…", "", "(this can take a few seconds — Esc to close)" }
                : _statusLines;
            return DashboardRenderer.BuildModal("status report" + (_statusRunning ? " (running…)" : ""), lines, _modalOffset, SafeWidth(), SafeHeight());
        }
        if (_modal != Modal.None)
        {
            List<string> lines;
            string title;
            lock (_gate) { lines = _modalLines; title = _modalTitle; }
            return DashboardRenderer.BuildModal(title, lines, _modalOffset, SafeWidth(), SafeHeight());
        }
        return DashboardRenderer.BuildRoot(BuildState());
    }

    private void StartStatusAgent()
    {
        if (_plan?.StatusAgent is not { } cfg || _statusRunning) { _modal = Modal.Status; return; }
        lock (_gate) { _modal = Modal.Status; _modalOffset = 0; _statusRunning = true; _statusLines = new(); }

        // Snapshot context on the UI thread; run the agent off-thread so the dashboard stays live.
        var snap = _snap with { Gates = _gates };
        var recentAgent = _agent.TakeLast(12).Select(a => $"{Glyph(a.Kind)} {a.Text}").ToList();
        var recentThinking = _thinking.Recent(8).Select(e => e.Text).ToList();
        var repo = _plan.Repo;

        // Fire-and-forget background probe; failures surface into the status pane, never silently.
        _ = Task.Run(() =>
        {
            string report;
            try
            {
                var git = GitView.Summary(repo);
                var prompt = StatusAgent.BuildPrompt(snap, git, recentAgent, recentThinking);
                report = StatusAgent.Run(cfg, prompt);
            }
            catch (Exception ex)
            {
                report = $"status agent failed: {ex.Message}";
            }
            lock (_gate)
            {
                _statusLines = report.Replace("\r\n", "\n").Split('\n').ToList();
                _statusRunning = false;
            }
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
                ConfirmPrompt = ConfirmGate.Message(_pendingConfirm),
            };
        }
    }

    private void PollKeys()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var ki = Console.ReadKey(intercept: true);
                if (_inputActive) { HandleInputKey(ki); continue; }
                var key = ki.Key;
                if (_modal != Modal.None) { HandleModalKey(key); continue; }
                switch (key)
                {
                    case ConsoleKey.T: OpenModal(Modal.Thinking); break;
                    case ConsoleKey.O: OpenModal(Modal.Output); break;
                    case ConsoleKey.D: OpenModal(Modal.Docs); break;
                    case ConsoleKey.V: OpenModal(Modal.Git); break;
                    case ConsoleKey.X: OpenModal(Modal.Prompt); break;
                    case ConsoleKey.I when _plan != null: _inputActive = true; _inputBuffer.Clear(); break;
                    case ConsoleKey.G when _plan?.StatusAgent is { Enabled: true }: StartStatusAgent(); break;
                    case ConsoleKey.P: _pendingConfirm = null; _keys.Enqueue(ControlAction.PauseAfterSession); break;
                    case ConsoleKey.R: _pendingConfirm = null; _keys.Enqueue(ControlAction.ResumeRun); break;
                    case ConsoleKey.A:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.AbortNow, ref _pendingConfirm);
                          if (act != null) { _keys.Enqueue(act.Value); Log("ABORT CONFIRMED"); }
                          else Log("Press A again to confirm ABORT (any other key cancels)"); }
                        break;
                    case ConsoleKey.S:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.SkipStage, ref _pendingConfirm);
                          if (act != null) { _keys.Enqueue(act.Value); Log("SKIP CONFIRMED"); }
                          else Log("Press S again to confirm SKIP (any other key cancels)"); }
                        break;
                    case ConsoleKey.K:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.KillSession, ref _pendingConfirm);
                          if (act != null) { _keys.Enqueue(act.Value); Log("KILL CONFIRMED"); }
                          else Log("Press K again to confirm KILL (any other key cancels)"); }
                        break;
                    case ConsoleKey.Q: _pendingConfirm = null; _keys.Enqueue(ControlAction.StopAfterSession); break;
                    case ConsoleKey.T or ConsoleKey.O or ConsoleKey.D or ConsoleKey.V or ConsoleKey.X or ConsoleKey.I or ConsoleKey.G:
                        break; // handled above — non-destructive keys don't cancel pending confirm
                    default: _pendingConfirm = null; break; // any unmapped key cancels
                }
            }
        }
        catch (InvalidOperationException) { /* input redirected */ }
    }

    private void HandleInputKey(ConsoleKeyInfo ki)
    {
        switch (ki.Key)
        {
            case ConsoleKey.Enter:
                var text = _inputBuffer.ToString().Trim();
                _inputActive = false;
                if (text.Length > 0 && _plan != null)
                {
                    try
                    {
                        var prev = InstructionQueue.List(_plan).LastOrDefault()?.File;
                        InstructionQueue.Write(_plan, text, prev);
                        Log($"[injected] queued instruction for next session: {text}");
                    }
                    catch (Exception ex) { Log($"[injected] failed to queue: {ex.Message}"); }
                }
                _inputBuffer.Clear();
                break;
            case ConsoleKey.Escape:
                _inputActive = false; _inputBuffer.Clear();
                break;
            case ConsoleKey.Backspace:
                if (_inputBuffer.Length > 0) _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                break;
            default:
                if (!char.IsControl(ki.KeyChar)) _inputBuffer.Append(ki.KeyChar);
                break;
        }
    }

    private void HandleModalKey(ConsoleKey key)
    {
        const int page = 12;
        var count = _modal == Modal.Status ? _statusLines.Count : _modalLines.Count;
        var max = Math.Max(0, count - 1);
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

    private static int SafeWidth() { try { return Math.Max(80, Console.WindowWidth); } catch (IOException) { return 120; } }
    private static int SafeHeight() { try { return Math.Max(24, Console.WindowHeight); } catch (IOException) { return 40; } }
}
