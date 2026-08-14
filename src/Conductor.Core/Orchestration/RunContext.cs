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
    /// <summary>KS2.6: under <c>--dry-run</c> this is a <c>MutedTelegramService</c> wrapping the real
    /// one — every push in the run path goes through this property, so a preview run cannot reach a
    /// phone through a call site nobody remembered to guard.</summary>
    public ITelegramService Telegram { get; }
    public WebhookNotifier Webhooks { get; }

    /// <summary>KS2.6: the gate every notification passes through — dry-run silence and the
    /// one-push-per-incident rate limit. See <see cref="ParkNotifier"/>.</summary>
    public ParkNotifier Notifier { get; }
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

    /// <summary>KS5.4: the cost ceiling this run is governed by — the plan's <c>limits.maxRunCostUsd</c>
    /// plus every dollar an owner has since approved on top of it. Read by the cap check, by
    /// <c>/state</c> and by the report line, so an operator, the wire and the park cannot be looking at
    /// three different ceilings. No cap configured means none: a grant cannot invent one.</summary>
    public decimal? EffectiveMaxRunCostUsd =>
        Budget.BudgetCeiling.EffectiveCostCap(Plan.Limits.MaxRunCostUsd, State.BudgetGrantUsd);

    /// <summary>KS5.4: the token half of the same ceiling. Both halves of one park move by the same
    /// machinery so they cannot diverge.</summary>
    public long? EffectiveMaxRunTokens =>
        Budget.BudgetCeiling.EffectiveTokenCap(Plan.Limits.MaxRunTokens, State.BudgetGrantTokens);

    /// <summary>KS5.4: the one answer to "which halves of its ceiling has this run reached" — the
    /// effective caps against the billed spend, through the single comparison in
    /// <see cref="Budget.BudgetCeiling.Standing"/>. The cap check parks on it, the reload un-parks on
    /// it, and the approval refuses on it, so the three cannot hold different opinions about the same
    /// run.</summary>
    public Budget.BudgetStanding BudgetStanding =>
        Budget.BudgetCeiling.Standing(EffectiveMaxRunCostUsd, BilledWindowUsd, EffectiveMaxRunTokens, RunTokens);

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

    /// <summary>KS5.2 — billed spend in this budget window by the model processes that are NOT the
    /// delivery agent: the advisor, analysis and fix lanes, the parallel audit, the auth probe. Kept
    /// beside <see cref="RunCostUsd"/> rather than folded into it so "what did the agent cost" stays a
    /// question with an answer.</summary>
    public decimal RunSideCostUsd { get; set; }

    /// <summary>KS5.2 — THE total. <see cref="RunCostUsd"/> alone was what
    /// <c>CheckBudgetCap</c> compared, so a run whose spend was all lanes and advisors could never reach
    /// its own ceiling; <see cref="RunOverheadUsd"/> is deliberately not in it, because gate overhead is
    /// an estimate from a plan-set rate and a ceiling must be reached by money somebody was charged.
    /// <c>/state</c> serves the same sum through <see cref="Models.RunState.BilledWindowCostUsd"/>, so
    /// the number an operator reads and the number the run parks on cannot drift apart.</summary>
    public decimal BilledWindowUsd => RunCostUsd + RunSideCostUsd;

    private Accounting.RunSpendLedger? _ledger;

    /// <summary>KS5.2 — where a model invocation outside the delivery agent turns into a
    /// <c>costs</c> row and an accrual. One instance per run so every spender writes through the same
    /// seam; see <see cref="Accounting.RunSpendLedger"/> for the session-key rule.</summary>
    public Accounting.RunSpendLedger Ledger => _ledger ??= new Accounting.RunSpendLedger(
        Store, State.RunId,
        accrue: r => { RunSideCostUsd += r.CostUsd; State.TotalSideCostUsd += r.CostUsd; PersistBudget(); },
        log: Log);
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
        IQaPolicy? qaPolicy = null,
        ParkNotifier? notifier = null)
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
        // KS2.6: derived from the run's own options unless a caller hands one in (the replay harness
        // does, to drive a dry-run LOOP with a live notifier and count what the flood used to send).
        Notifier = notifier ?? new ParkNotifier(options.DryRun, plan.Limits.MaxPushesPerIncident);
        Telegram = Notifier.DryRun ? new MutedTelegramService(telegram) : telegram;
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
        RunSideCostUsd = State.PerRunSideCostUsd;
    }

    /// <summary>
    /// KS5.2 — take in the billed rows this process did NOT write, so a cap governs every dollar spent
    /// against the run and not only the ones the loop thread happened to spend itself.
    /// <para>Two writers reach the <c>costs</c> table from outside the run loop: the <c>watch</c>
    /// supervisor, which is a different PROCESS, and the control plane's advisor spawns
    /// (<c>/tasks/refine</c>, <c>/tasks/split</c>), which are HTTP threads that must not touch the
    /// loop's counters. Both wrote their row and stopped, on the belief that "the cap sees it the next
    /// time the run is priced from its database". Nothing ever re-priced a run from its database —
    /// <see cref="RestoreBudget"/> reads run STATE — so those dollars could never trip
    /// <c>limits.maxRunCostUsd</c>. This is the missing half, and it is why the sentence is now true.</para>
    /// <para>The arithmetic is a difference, not a re-derivation:
    /// <see cref="Models.RunState.TotalSideCostUsd"/> is what this engine has already accrued over the
    /// run's lifetime, and the table holds that plus whatever anyone else wrote. Only the excess is
    /// taken in, so calling it every boundary is idempotent, and the window
    /// (<see cref="RunSideCostUsd"/>) moves by exactly the same amount as the lifetime total.</para>
    /// <para>Best-effort by construction: an unreadable table answers 0 and the run carries on. A
    /// reconciliation must never be the reason a run stops.</para>
    /// </summary>
    public void AbsorbOutOfProcessSpend()
    {
        if (Store is not { } s || string.IsNullOrEmpty(State.RunId)) return;
        var external = s.SumSideSpendUsd(State.RunId) - State.TotalSideCostUsd;
        if (external <= 0m) return;

        RunSideCostUsd += external;
        State.TotalSideCostUsd += external;
        PersistBudget();
        Log($"side spend reconciled: ${external:0.0000} billed by a writer outside this loop " +
            "(watch supervisor / control-plane advisor) — now counted against the run cap");
    }

    /// <summary>Persist current run-cost state back into RunState.</summary>
    public void PersistBudget()
    {
        State.PerRunCostUsd = RunCostUsd;
        State.PerRunTokens = RunTokens;
        State.PerRunOverheadCostUsd = RunOverheadUsd;
        State.PerRunSideCostUsd = RunSideCostUsd;
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

    /// <summary>True once <see cref="EnsureRunRow"/> has written the row in this process.</summary>
    private bool _runRowWritten;

    /// <summary>
    /// KS0.3, bug #27 — the <c>runs</c> row is the FK target of every other table, so it has to exist
    /// before anything else is written.
    /// <para>It did not. <c>run_state</c> declares <c>FOREIGN KEY (run_id) REFERENCES runs(run_id)</c>
    /// and the run loop saved state before it initialised the run, so on a brand-new database the
    /// first save was rejected: <c>SQLite Error 19</c>, swallowed by <c>TryExecute</c>, logged at
    /// Error. Every fresh run opened with a database error in its log AND lost its first state write.
    /// Measured live on a rig that had never run: one FK line, straight after "started paused".</para>
    /// <para>Fixed here rather than by reordering the one call that happened to be second, because the
    /// ordering is what was fragile: <c>Save()</c> is called from a dozen places and a new one arrives
    /// every era. Ensuring the row at the funnel means a future early save cannot reintroduce this.
    /// Idempotent per process; <c>InitializeRun</c> is itself an upsert that refreshes the engine stamp
    /// and limits, so a resume still records what is driving it now.</para>
    /// </summary>
    public void EnsureRunRow()
    {
        if (_runRowWritten || Store is not { } s || string.IsNullOrEmpty(State.RunId)) return;
        _runRowWritten = true;
        s.InitializeRun(State.RunId, Plan.Name, Plan.Repo, Git.Branch(Plan.Repo),
                        EngineStamp.Current, RunLimitsSnapshot.From(Plan.Limits).ToJson());
    }

    /// <summary>Persist RunState through the store.</summary>
    public void Save()
    {
        if (Store is { } s)
        {
            EnsureRunRow();
            var json = System.Text.Json.JsonSerializer.Serialize(State, Models.PlanConfig.JsonOpts);
            s.SaveRunState(State.RunId, State.PlanName, json);
            SyncRunStatus(s);
        }
    }

    /// <summary>The last <c>runs.status</c> this process wrote. Save() is called several times per
    /// session and every 800ms while parked; the column only needs the transitions.</summary>
    private string? _runStatusWritten;

    /// <summary>
    /// KS0.2, closing FU-F1-06 — keep <c>runs.status</c> honest about a run that is parked.
    /// <para>The row was written twice in a run's life: <c>running</c> at every process start, and a
    /// terminal word at completion. A run that stopped <c>NeedsHuman</c> or <c>Paused</c> — the two
    /// most common ways a run stops — therefore said <c>running</c> for ever, which is why four rows
    /// on this machine claim to be live runs of engines that exited weeks ago. state.json knew; the
    /// row did not, and the row is what every other machine reads.</para>
    /// <para>Here rather than at each transition because there are a dozen of those and a new one
    /// arrives every era: Save() is what they all already call, so a park invented next year is
    /// covered without anyone remembering this. Terminal states are left to
    /// <see cref="IRunStore.RecordRunEnd"/>, which also stamps <c>ended_utc</c>.</para>
    /// </summary>
    private void SyncRunStatus(IRunStore s)
    {
        if (string.IsNullOrEmpty(State.RunId)) return;
        if (State.Status is RunStatus.Completed or RunStatus.Aborted) return;

        var text = RunRecord.StatusText(State.Status);
        if (string.Equals(_runStatusWritten, text, StringComparison.Ordinal)) return;
        s.UpdateRunStatus(State.RunId, text);
        _runStatusWritten = text;
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
