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
    public IProgressProvider Progress { get; }
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
    public long? EffectiveMaxSessionTokens => State.MaxSessionTokensThisRun switch
    {
        0 => null,
        { } thisRun => thisRun,
        null => Plan.Limits.MaxSessionTokens,
    };

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
    public DateTime? LastBgLivenessCheck { get; set; }
    public bool CachedBgAlive { get; set; }

    // ── correlation scope (log enrichment) ──

    public string? CurGate { get; set; }
    public string? Outcome { get; set; }

    // ── activity + decomposition ──

    public List<(string Kind, string Text, DateTime Utc)> Activity { get; } = new();
    public HashSet<string> DecomposedCheckpoints { get; } = new(StringComparer.Ordinal);

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
        LockPath = Path.Combine(plan.StateDir, "conductor.lock");
        ControlPath = Path.Combine(plan.StateDir, "control.json");
        LogPath = Path.Combine(plan.StateDir, "conductor.log");
    }

    /// <summary>G3.2 live plan reload: swap the plan every satellite reads through this context, and
    /// rebuild the prompt builder (it caches the plan + persona registry). MUST only be called from
    /// the run loop at a session boundary — never while an agent session is running against the old
    /// stage graph. Callers are responsible for also swapping satellites that hold their own plan
    /// reference (GateOrchestrator, LaneCoordinator, ControlDispatcher).</summary>
    public void SwapPlan(PlanConfig fresh)
    {
        Plan = fresh;
        Prompts = new PromptBuilder(fresh, new PersonaRegistry(fresh), Lessons, Qa);
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

    /// <summary>Read tracker (defensive — returns empty snapshot on failure).</summary>
    public TrackerSnapshot ReadTrackerSafe()
    {
        try { return Progress.Read(Plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }
}
