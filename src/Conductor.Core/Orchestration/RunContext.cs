using System.Collections.Concurrent;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Orchestration;

/// <summary>
/// Shared mutable state + immutable references carried by every orchestration component.
/// Each satellite (RunLoop, SessionRunner, VerdictEngine) receives this one bag instead of
/// 15+ individual delegates — the same pattern that broke the god-class cycle in F5 (F5.3).
/// </summary>
public sealed class RunContext
{
    // ── immutable config + references ──
    // (Plan/Prompts are reassignable through SwapPlan ONLY — the G3.2 live-reload session boundary.)

    public PlanConfig Plan { get; private set; }
    public RunState State { get; }
    public RunOptions Options { get; }
    public IProgressSink Sink { get; }
    public IEventSink Events { get; }
    public LessonsManager Lessons { get; }
    public PromptBuilder Prompts { get; private set; }
    public IPlanner Planner { get; }
    public IProgressProvider Progress { get; private set; }
    public IAgentProvider AgentProvider { get; }
    public IRunStore? Store { get; }
    public ProcessSupervisor? ProcessSupervisor { get; }
    public ConcurrentQueue<ControlCommand>? ControlInbox { get; }
    public ITelegramService Telegram { get; }
    public WebhookNotifier Webhooks { get; }
    public IWorkflowResolver Workflows { get; }
    public IAssignmentPolicy Assignments { get; }
    public IQaPolicy Qa { get; }

    // ── file paths ──

    public string StateDir { get; }
    public string LockPath { get; }
    public string ControlPath { get; }
    public string LogPath { get; }

    // ── mutable run-loop state ──

    /// <summary>P5: the per-session token cap the rollover machinery actually enforces — the
    /// session-scoped this-run override when set (0 = forced off), else the plan's
    /// <c>limits.maxSessionTokens</c>. null = rollover off. Every rollover/soft-break read goes
    /// through here so the two knobs can never disagree.</summary>
    public long? EffectiveMaxSessionTokens =>
        SoftBreak.EffectiveCap(State.MaxSessionTokensThisRun, Plan.Limits.MaxSessionTokens);

    /// <summary>W4.4: the QA override of the item a session is working on — the first not-done
    /// checkpoint of the stage in the PRE-session snapshot, which is exactly the item the assignment
    /// policy claims. Empty when the card has no override (the common case), so every caller
    /// projects identically to the stage dial unless someone set one. Best-effort: no store, no
    /// graph, no override.</summary>
    public string ItemQaFor(StageConfig stage, TrackerSnapshot? preTrack)
    {
        if (Store == null || preTrack == null || stage == null) return "";
        var itemId = preTrack.ForStage(stage.Id).FirstOrDefault(c => c.IsOpen)?.Id;
        if (string.IsNullOrEmpty(itemId)) return "";
        try
        {
            var graph = new TaskGraph();
            graph.Fold(Store.ReadAllEvents(State.RunId));
            return graph.Find(itemId)?.Qa ?? "";
        }
        catch (InvalidOperationException) { return ""; }
    }

    public IReadOnlyList<GateResult>? LastGates { get; set; }
    public DateTime? LastControlWrite { get; set; }
    public DateTime? BackoffUntil { get; set; }
    public DateTime? StallBackoffUntil { get; set; }
    public int StallBackoffMultiplier { get; set; } = 1;
    public DateTime? DnsParkedUntil { get; set; }
    public int PreflightConsecutiveFailures { get; set; }
    public bool SoftBreakSignalled { get; set; }
    public bool SessionApproved { get; set; }
    public decimal RunCostUsd { get; set; }
    public long RunTokens { get; set; }
    public decimal RunOverheadUsd { get; set; }
    // W3.1: the bg-liveness cache moved into the SessionWatchdog's closure — it is read and written
    // on the watchdog thread only, so it must not live in state the poll loop also touches.

    // ── correlation scope (log enrichment) ──

    public string? CurGate { get; set; }
    public string? Outcome { get; set; }

    // ── activity + decomposition ──

    public List<(string Kind, string Text, DateTime Utc)> Activity { get; } = new();
    public HashSet<string> DecomposedCheckpoints { get; } = new(StringComparer.Ordinal);

    // ── live transcript (the Face agent pane's /transcript/current feed) ──

    private TranscriptLog? _transcript;

    /// <summary>The run-scoped transcript writer. Created on first agent output (a dry run never
    /// touches disk); rotates away another run's file so the Face never replays a previous era.
    /// Disposed by the Orchestrator at run end via <see cref="DisposeTranscript"/>.</summary>
    public TranscriptLog Transcript =>
        _transcript ??= TranscriptLog.OpenForRun(Path.Combine(StateDir, "transcript.jsonl"), State.RunId);

    /// <summary>Flush + close the transcript feed if it was ever opened. Safe to call twice.</summary>
    public void DisposeTranscript()
    {
        _transcript?.Dispose();
        _transcript = null;
    }

    // ── structured logging ──

    public ILogger Logger { get; }

    public RunContext(
        PlanConfig plan,
        RunState state,
        RunOptions options,
        IProgressSink sink,
        IEventSink events,
        PromptBuilder prompts,
        LessonsManager lessons,
        IPlanner planner,
        IProgressProvider progress,
        IAgentProvider agentProvider,
        IRunStore? store,
        ProcessSupervisor? processSupervisor,
        ConcurrentQueue<ControlCommand>? controlInbox,
        ITelegramService telegram,
        WebhookNotifier webhooks,
        IWorkflowResolver? workflowResolver,
        ILogger logger,
        IAssignmentPolicy? assignmentPolicy = null,
        IQaPolicy? qaPolicy = null)
    {
        Plan = plan;
        State = state;
        Options = options;
        Sink = sink;
        Events = events;
        Prompts = prompts;
        Lessons = lessons;
        Planner = planner;
        Progress = progress;
        AgentProvider = agentProvider;
        Store = store;
        ProcessSupervisor = processSupervisor;
        ControlInbox = controlInbox;
        Telegram = telegram;
        Webhooks = webhooks;
        Workflows = workflowResolver ?? new WorkflowEngine();
        Assignments = assignmentPolicy ?? new DefaultAssignmentPolicy();
        Qa = qaPolicy ?? new DefaultQaPolicy();
        Logger = logger;
        StateDir = plan.StateDir;
        LockPath = EngineLock.PathFor(plan.StateDir);
        ControlPath = Path.Combine(plan.StateDir, "control.json");
        LogPath = Path.Combine(plan.StateDir, "conductor.log");
    }

    /// <summary>G3.2 live plan reload: swap the plan every satellite reads through this context, and
    /// rebuild the prompt builder (it caches the plan + persona registry). MUST only be called from
    /// the run loop at a session boundary — never while an agent session is running against the old
    /// stage graph. Callers are responsible for also swapping satellites that hold their own plan
    /// reference (GateOrchestrator, LaneCoordinator, ControlDispatcher, the control plane).</summary>
    public void SwapPlan(PlanConfig fresh)
    {
        Plan = fresh;
        Prompts = new PromptBuilder(fresh, new PersonaRegistry(fresh), Lessons, Qa);
        // W5.1: the progress provider is built FROM the plan and the inline one captures its
        // checkpoint list by value — a card declared mid-run (or a switch of progress.kind) was
        // invisible to every declared read until the process restarted.
        Progress = Planning.ProgressProviderFactory.Create(fresh);
    }

    // ── convenience delegations ──

    /// <summary>Restore run-cost state from RunState (called at start + after crash recovery).</summary>
    public void RestoreBudget()
    {
        RunCostUsd = State.PerRunCostUsd;
        RunTokens = State.PerRunTokens;
        RunOverheadUsd = State.PerRunOverheadCostUsd;
    }

    /// <summary>Persist current run-cost state back into RunState.</summary>
    public void PersistBudget()
    {
        State.PerRunCostUsd = RunCostUsd;
        State.PerRunTokens = RunTokens;
        State.PerRunOverheadCostUsd = RunOverheadUsd;
    }

    /// <summary>Plain-text log line (console + file + structured).</summary>
    public void Log(string line) => Log(line, null);

    /// <summary>Plain-text log line with outcome annotation for structured scope.</summary>
    public void Log(string line, string? outcome)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { File.AppendAllText(LogPath, stamped + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var prevOutcome = Outcome;
        Outcome = outcome;
        try
        {
            using (BeginCorrelationScope())
                Logger.LogInformation("{ConductorMessage}", line);
        }
        finally { Outcome = prevOutcome; }

        Sink.Log(stamped);
    }

    /// <summary>Log a line with outcome annotation.</summary>
    public void LogWithOutcome(string line, string? outcome) => Log(line, outcome);

    /// <summary>Push a correlation scope onto the structured log context.</summary>
    public IDisposable? BeginCorrelationScope()
    {
        var scope = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(State.RunId)) scope["runId"] = State.RunId;
        if (State.SessionCounter > 0) scope["sessionId"] = State.SessionCounter.ToString();
        if (!string.IsNullOrEmpty(State.CurrentStage)) scope["stage"] = State.CurrentStage;
        if (CurGate != null) scope["gate"] = CurGate;
        if (Outcome != null) scope["outcome"] = Outcome;
        return scope.Count > 0 ? Logger.BeginScope(scope) : null;
    }

    /// <summary>Persist RunState through the store.</summary>
    public void Save()
    {
        if (Store is { } s)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(State, Models.PlanConfig.JsonOpts);
            s.SaveRunState(State.RunId, State.PlanName, json);
        }
    }

    /// <summary>Read tracker (defensive — returns empty snapshot on failure). This is the DECLARED
    /// work, statuses included: the sync's input, the handoff block's home, and the base the verdict
    /// diffs a hand-edited tracker against. For "what is the state of the work", use
    /// <see cref="ReadWork"/> instead.</summary>
    public TrackerSnapshot ReadTrackerSafe()
    {
        try { return Progress.Read(Plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    /// <summary>SF4.2 — a queue item that arrives while the owner is away from the keyboard reaches
    /// them. That is the entire point of the surface: `.conductor/OWNER-QUEUE.md` and
    /// <c>GET /owner/queue</c> both require someone to be LOOKING, and the case this era was written
    /// for is the one where nobody is.
    /// <para>Only NEW items — <see cref="OwnerQueue.Write"/> diffs against what the previous render
    /// already announced — so a run that writes its report twenty times in a session pushes once per
    /// obligation, not twenty times. Each line carries the two things that make an alert actionable
    /// rather than merely alarming: what it unblocks, and the exact command that clears it.</para>
    /// <para>It lives on the context rather than on <c>RunLoop</c> because the report write path has
    /// four call sites across three classes (RunLoop, SessionRunner, Orchestrator); a private method
    /// on one of them would leave the other two silently unable to announce anything.</para></summary>
    public void NotifyNewOwnerQueueItems(IReadOnlyList<OwnerQueueItem> items)
    {
        if (items is not { Count: > 0 }) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(FormattableString.Invariant(
            $"<b>Owner queue: {items.Count} new item{(items.Count == 1 ? "" : "s")}</b>"));
        foreach (var item in items)
        {
            sb.AppendLine();
            sb.AppendLine(EscapeHtml(item.Title));
            sb.AppendLine($"unblocks: {EscapeHtml(item.Unblocks)}");
            sb.AppendLine(item.Command.Length > 0
                ? $"clears with: <code>{EscapeHtml(item.Command)}</code>"
                : "clears with: nothing to type — it clears itself");
        }

        Log($"owner queue: {items.Count} new item(s) — {string.Join("; ", items.Select(i => i.Title))}");
        _ = Telegram.PushAsync(sb.ToString().TrimEnd());
    }

    private static string EscapeHtml(string s)
        => s.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>W5.1: the work snapshot the engine SCHEDULES on — declared rows carrying the work
    /// graph's status, the same projection the Face's board and sidebar read. See
    /// <see cref="Planning.WorkSnapshot"/> for why the declared statuses cannot be the answer.</summary>
    public TrackerSnapshot ReadWork()
        => Planning.WorkSnapshot.Read(Store, State.RunId, ReadTrackerSafe);
}
