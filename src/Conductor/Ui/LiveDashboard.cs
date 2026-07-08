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
    private enum Modal { None, Thinking, Output, Docs, Git, Prompt, Status, Timeline }

    private readonly Lock _gate = new();
    private readonly List<DashboardState.AgentLine> _agent = new();
    private readonly ReasoningBuffer _thinking = new();
    private readonly List<LogEntry> _log = new();
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

    private PlanTreeView _tree = new();
    private bool _agentExpanded;

    // Command-history search/filter for the Output modal (B4.6). The raw feed is captured once on
    // open; category + typed search re-filter it live on the UI thread.
    private HistoryCategory _historyCategory = HistoryCategory.All;
    private readonly StringBuilder _historySearch = new();
    private bool _historyTyping;
    private IReadOnlyList<HistoryEntry> _historyRaw = Array.Empty<HistoryEntry>();

    private volatile bool _statusRunning;
    private List<string> _statusLines = new();

    public LiveDashboard(PlanConfig? plan = null) => _plan = plan;

    public void Log(string line) => Log(line, LogSeverity.Info);

    /// <summary>Logs an operator-facing line with an explicit severity so the footer log colour-codes it
    /// (B4.4). The dashboard's own control feedback — destructive-action confirmations, injection
    /// success/failure — carries severity here so the severity model is actually exercised in real runs,
    /// not only via the <see cref="IProgressSink.Log(LogEntry)"/> producer path.</summary>
    private void Log(string line, LogSeverity severity)
    {
        lock (_gate)
        {
            _log.Add(new LogEntry(line, DateTime.UtcNow, severity));
            if (_log.Count > 300) _log.RemoveRange(0, 100);
        }
    }

    /// <summary>Log with explicit severity (B4.4). The UI thread captures the structured entry for colour-coded display.</summary>
    void IProgressSink.Log(LogEntry entry)
    {
        lock (_gate)
        {
            _log.Add(new LogEntry(entry.Text, entry.Utc, entry.Severity));
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
                    try { if (Console.KeyAvailable) { HandlePreviewKey(Console.ReadKey(intercept: true)); if (_quitPreview) break; } }
                    catch (InvalidOperationException) { break; }
                    ctx.UpdateTarget(BuildTarget());
                    ctx.Refresh();
                    Thread.Sleep(120);
                }
            });
    }

    private bool _quitPreview;
    private void HandlePreviewKey(ConsoleKeyInfo ki)
    {
        if (_modal != Modal.None) { HandleModalKey(ki); return; }
        switch (ki.Key)
        {
            case ConsoleKey.T: OpenModal(Modal.Thinking); break;
            case ConsoleKey.O: OpenModal(Modal.Output); break;
            case ConsoleKey.D: OpenModal(Modal.Docs); break;
            case ConsoleKey.V: OpenModal(Modal.Git); break;
            case ConsoleKey.X: OpenModal(Modal.Prompt); break;
            case ConsoleKey.L: OpenModal(Modal.Timeline); break;
            case ConsoleKey.G when _plan?.StatusAgent is { Enabled: true }: StartStatusAgent(); break;
            case ConsoleKey.F: _tree = _tree with { Filter = PlanTree.NextFilter(_tree.Filter) }; break;
            case ConsoleKey.E: _tree = _tree with { ExpandAll = !_tree.ExpandAll }; break;
            case ConsoleKey.UpArrow: MoveTreeSelection(-1); break;
            case ConsoleKey.DownArrow: MoveTreeSelection(+1); break;
            case ConsoleKey.C: _agentExpanded = !_agentExpanded; break;
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

        // Capture the run context while holding the lock: _agent/_thinking/_snap/_gates are mutated by
        // producer threads (AgentEvent/Snapshot/GateProgress), so reading them off the UI thread without
        // the gate races the producers — e.g. _agent.TakeLast enumerating while a producer Adds throws
        // "Collection was modified". Materialise the immutable inputs here, then run the agent off-thread.
        DashboardSnapshot snap;
        List<string> recentAgent, recentThinking;
        lock (_gate)
        {
            _modal = Modal.Status; _modalOffset = 0; _statusRunning = true; _statusLines = new();
            snap = _snap with { Gates = _gates };
            recentAgent = _agent.TakeLast(12).Select(a => $"{Glyph(a.Kind)} {a.Text}").ToList();
            recentThinking = _thinking.Recent(8).Select(e => e.Text).ToList();
        }
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
                Tree = _tree,
                AgentExpanded = _agentExpanded,
            };
        }
    }

    /// <summary>Move the plan-tree selection cursor (↑/↓) over the currently visible rows, off the same
    /// per-stage roll-up the tree renders. Selection drives doc-on-select (the <c>D</c> key), B4.7.</summary>
    private void MoveTreeSelection(int delta)
    {
        lock (_gate)
        {
            var stages = DashboardRenderer.StagesFor(_snap);
            _tree = _tree with { Selected = PlanTree.MoveSelection(stages, _tree, delta) };
        }
    }

    /// <summary>The stage whose doc section the <c>D</c> pop-out should show: the owning stage of the
    /// selected plan-tree row, falling back to the running stage when nothing is selected (B4.7).</summary>
    private string SelectedDocStage()
    {
        var stages = DashboardRenderer.StagesFor(_snap);
        var stage = PlanTree.StageForRow(stages, _tree.Selected);
        return string.IsNullOrEmpty(stage) ? _snap.StageId : stage;
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
                if (_modal != Modal.None) { HandleModalKey(ki); continue; }
                switch (key)
                {
                    case ConsoleKey.T: OpenModal(Modal.Thinking); break;
                    case ConsoleKey.O: OpenModal(Modal.Output); break;
                    case ConsoleKey.D: OpenModal(Modal.Docs); break;
                    case ConsoleKey.V: OpenModal(Modal.Git); break;
                    case ConsoleKey.X: OpenModal(Modal.Prompt); break;
                    case ConsoleKey.L: OpenModal(Modal.Timeline); break;
                    case ConsoleKey.I when _plan != null: _inputActive = true; _inputBuffer.Clear(); break;
                    case ConsoleKey.G when _plan?.StatusAgent is { Enabled: true }: StartStatusAgent(); break;
                    case ConsoleKey.F: _tree = _tree with { Filter = PlanTree.NextFilter(_tree.Filter) }; break;
                    case ConsoleKey.E: _tree = _tree with { ExpandAll = !_tree.ExpandAll }; break;
                    case ConsoleKey.UpArrow: MoveTreeSelection(-1); break;
                    case ConsoleKey.DownArrow: MoveTreeSelection(+1); break;
                    case ConsoleKey.C: _agentExpanded = !_agentExpanded; break;
                    case ConsoleKey.P: _pendingConfirm = null; _keys.Enqueue(ControlAction.PauseAfterSession); break;
                    case ConsoleKey.R: _pendingConfirm = null; _keys.Enqueue(ControlAction.ResumeRun); break;
                    case ConsoleKey.A:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.AbortNow, ref _pendingConfirm);
                          if (act != null) { _keys.Enqueue(act.Value); Log("ABORT CONFIRMED", LogSeverity.Warn); }
                          else Log("Press A again to confirm ABORT (any other key cancels)", LogSeverity.Waiting); }
                        break;
                    case ConsoleKey.S:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.SkipStage, ref _pendingConfirm);
                          if (act != null) { _keys.Enqueue(act.Value); Log("SKIP CONFIRMED", LogSeverity.Warn); }
                          else Log("Press S again to confirm SKIP (any other key cancels)", LogSeverity.Waiting); }
                        break;
                    case ConsoleKey.K:
                        { var act = ConfirmGate.ProcessDestructive(ControlAction.KillSession, ref _pendingConfirm);
                          if (act != null) { _keys.Enqueue(act.Value); Log("KILL CONFIRMED", LogSeverity.Warn); }
                          else Log("Press K again to confirm KILL (any other key cancels)", LogSeverity.Waiting); }
                        break;
                    case ConsoleKey.Q: _pendingConfirm = null; _keys.Enqueue(ControlAction.StopAfterSession); break;
                    case ConsoleKey.T or ConsoleKey.O or ConsoleKey.D or ConsoleKey.V or ConsoleKey.X or ConsoleKey.L or ConsoleKey.I or ConsoleKey.G:
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
                        Log($"[injected] queued instruction for next session: {text}", LogSeverity.Success);
                    }
                    catch (Exception ex) { Log($"[injected] failed to queue: {ex.Message}", LogSeverity.Error); }
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

    private void HandleModalKey(ConsoleKeyInfo ki)
    {
        if (_modal == Modal.Output && HandleHistoryKey(ki)) return;
        var key = ki.Key;
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

    /// <summary>Search/filter keys for the command-history (Output) modal (B4.6). Returns true when the
    /// key was consumed; false lets it fall through to the shared scroll/close handling. While typing a
    /// search, printable keys append (so Esc/q/arrows don't leak into scroll/close).</summary>
    private bool HandleHistoryKey(ConsoleKeyInfo ki)
    {
        if (_historyTyping)
        {
            switch (ki.Key)
            {
                case ConsoleKey.Enter or ConsoleKey.Escape:
                    _historyTyping = false; lock (_gate) RebuildHistoryLocked(); return true;
                case ConsoleKey.Backspace:
                    if (_historySearch.Length > 0) _historySearch.Remove(_historySearch.Length - 1, 1);
                    lock (_gate) RebuildHistoryLocked(); return true;
                default:
                    if (!char.IsControl(ki.KeyChar)) { _historySearch.Append(ki.KeyChar); lock (_gate) RebuildHistoryLocked(); }
                    return true;
            }
        }
        if (ki.KeyChar == '/') { _historyTyping = true; lock (_gate) RebuildHistoryLocked(); return true; }
        if (ki.Key == ConsoleKey.Tab)
        {
            _historyCategory = CommandHistory.NextCategory(_historyCategory);
            lock (_gate) RebuildHistoryLocked();
            return true;
        }
        return false;
    }

    private void OpenModal(Modal kind)
    {
        if (kind == Modal.Output) { OpenHistory(); return; }
        var (title, lines) = kind switch
        {
            Modal.Thinking => ("thinking (full reasoning)", ThinkingLines()),
            Modal.Docs => ($"docs · stage {SelectedDocStage()}", DocsLines()),
            Modal.Git => ("git", GitLines()),
            Modal.Prompt => ("compiled prompt (current session)", PromptLines()),
            Modal.Timeline => ("timeline · transitions from the event log", TimelineLines()),
            _ => ("", new List<string>()),
        };
        lock (_gate) { _modal = kind; _modalTitle = title; _modalLines = lines; _modalOffset = Math.Max(0, lines.Count - 1); }
    }

    /// <summary>Opens the command-history modal (B4.6): captures the merged agent+thinking feed once,
    /// resets the query, and renders the unfiltered feed. Tab filters by category, `/` searches.</summary>
    private void OpenHistory()
    {
        lock (_gate)
        {
            _historyCategory = HistoryCategory.All;
            _historySearch.Clear();
            _historyTyping = false;
            _historyRaw = _agent.Select(a => new HistoryEntry(a.Kind, a.Text, a.Utc))
                .Concat(_thinking.All().Select(e => new HistoryEntry("thinking", e.Text, e.Utc)))
                .OrderBy(e => e.Utc)
                .ToList();
            _modal = Modal.Output;
            RebuildHistoryLocked();
        }
    }

    /// <summary>Re-filters the captured feed against the current category + typed search and rebuilds
    /// the modal lines/title. Caller holds <c>_gate</c>. A typed <c>/category</c> token wins over the
    /// Tab-cycled category; the search box jumps to the top when the query narrows.</summary>
    private void RebuildHistoryLocked()
    {
        var parsed = CommandHistory.Parse(_historySearch.ToString());
        var category = parsed.Category != HistoryCategory.All ? parsed.Category : _historyCategory;
        var query = new HistoryQuery(category, parsed.Search);

        _modalLines = CommandHistory.Filter(_historyRaw, query)
            .SelectMany(e => Split($"{e.Utc.ToLocalTime():HH:mm:ss} {Glyph(e.Kind)} {e.Text}"))
            .DefaultIfEmpty(query.IsActive ? "(no history matches this filter)" : "(no agent output yet)")
            .ToList();

        var search = _historyTyping ? _historySearch + "▌" : query.Search;
        _modalTitle = $"command history · filter {CommandHistory.CategoryLabel(category)}" +
                      (search.Length > 0 ? $" · /{search}" : "") +
                      (_historyTyping ? "  (Enter/Esc done)" : "  (/ search · Tab filter · Esc close)");
        _modalOffset = query.IsActive ? 0 : Math.Max(0, _modalLines.Count - 1);
    }

    // ---- modal content providers (captured once on open) ----

    private List<string> ThinkingLines()
    {
        lock (_gate)
            return _thinking.All()
                .SelectMany(e => Split($"{e.Utc.ToLocalTime():HH:mm:ss} ~ {e.Text}"))
                .DefaultIfEmpty("(no thinking captured yet)").ToList();
    }

    private List<string> DocsLines()
    {
        if (_plan == null) return new() { "(docs unavailable in preview)" };
        var stageId = SelectedDocStage();
        var path = Path.Combine(_plan.Repo, _plan.PlanDoc);
        var section = DocsExtractor.ForStageFromFile(path, stageId);
        if (string.IsNullOrWhiteSpace(section))
            return new() { $"(no section for {stageId} found in {_plan.PlanDoc})", "", $"doc: {path}" };
        return Split(section);
    }

    private List<string> GitLines()
        => _plan == null ? new() { "(git unavailable in preview)" } : Split(GitView.Summary(_plan.Repo));

    /// <summary>Folds the append-only event log into the timeline modal (B5.1) — transitions with
    /// durations, from the same <c>events.jsonl</c> the REPORT.md Timeline section reads. Captured once
    /// on open; tolerant of a missing/locked log (renders a hint rather than throwing).</summary>
    private List<string> TimelineLines()
    {
        if (_plan == null) return new() { "(timeline unavailable in preview)" };
        var entries = Reporter.ReadTimeline(_plan);
        if (entries.Count == 0) return new() { "(no events recorded yet — the timeline populates as the run emits events)" };
        return entries.Select(Conductor.Core.Events.Timeline.Format).ToList();
    }

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
