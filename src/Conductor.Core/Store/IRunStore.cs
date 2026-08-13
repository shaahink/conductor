using Conductor.Core.Events;

namespace Conductor.Core.Store;

/// <summary>
/// Single source of truth for all persisted run data. Implemented by <see cref="SqliteRunStore"/>.
/// Every write that fails emits a <see cref="DatabaseWriteFailed"/> event and logs at
/// Error level; callers that cannot tolerate a swallowed write must check the event stream.
/// </summary>
public interface IRunStore : IDisposable
{
    // ---------------------------------------------------------------- run lifecycle

    /// <summary>K3.3: <paramref name="engine"/> replaces the old <c>driverVersion</c> string, which
    /// carried the assembly version — the same 2.0.0.0 for every build ever made. <paramref name="limitsJson"/>
    /// is <see cref="RunLimitsSnapshot"/> as JSON, or null when the caller has no plan to read.</summary>
    void InitializeRun(string runId, string planName, string repo, string? branch,
                       EngineStamp engine, string? limitsJson = null);
    void RecordRunEnd(string runId, string status);

    /// <summary>KS0.2, closing FU-F1-06 — write <c>runs.status</c> and NOTHING else. The column had no
    /// status-only writer, so a run that stopped in a resumable state (<c>NeedsHuman</c>,
    /// <c>Paused</c>, <c>AwaitingOwner</c>) went on saying <c>running</c> for ever;
    /// <see cref="RecordRunEnd"/> could not be used for those because it also stamps
    /// <c>ended_utc</c>, and a run that can still be resumed has not ended. Vocabulary comes from
    /// <see cref="RunRecord.StatusText"/>.</summary>
    void UpdateRunStatus(string runId, string status);

    /// <summary>KS0.2 — close a run record that no engine will ever close itself, with the instant it
    /// actually stopped rather than the instant an operator noticed. Returns the number of rows
    /// changed, so naming a run that is not in this store is an answer and not a silent success —
    /// which is the difference between this and every other write here.</summary>
    int CloseRunRecord(string runId, string status, DateTimeOffset endedUtc);

    // ---------------------------------------------------------------- stage lifecycle

    void InitializeStage(string runId, string stageId, string title);
    void ConfirmStage(string runId, string stageId);

    // ---------------------------------------------------------------- session lifecycle

    void RecordSession(
        string runId, string stageId, int number, string kind,
        DateTime startedUtc, DateTime? endedUtc, string? outcome,
        string? agentSessionId, int resumeCount, int attempt,
        string? gateSummary, string? resultSummary, int commitCount, string? newlyDone,
        string? digest = null, string? softBreak = null,
        string? engine = null, string? limits = null,
        Conductor.Core.Events.ContextWindowStats? context = null);

    // ---------------------------------------------------------------- costs

    void RecordCost(
        string runId, int sessionNumber, string category,
        long tokensIn, long tokensOut, long tokensThink, long tokensCache,
        decimal costUsd, long wallMs);

    // ---------------------------------------------------------------- gates

    void RecordGate(
        string runId, int? sessionNumber, string? stageId,
        string name, string tier, string scope, string? sha,
        bool passed, bool skipped, bool optional, int exitCode, long durationMs, string? tail);

    bool? GetLastPassingGateResult(string runId, string gateName, string tier, string sha);

    /// <summary>SC4.1: how long this gate took the last time it genuinely passed, in ms, for the
    /// comparison a failure line has to carry. Skipped and cached rows are excluded — they measure
    /// nothing. Null when this run has no passing run of the gate on record.</summary>
    long? GetLastPassingGateDurationMs(string runId, string gateName, string tier);

    // ---------------------------------------------------------------- scores

    void WriteScore(string runId, int sessionNumber, string? stageId, int score, string verdict, string findings);

    /// <summary>SF1.1: every verifier verdict this run recorded, newest session first. Behind
    /// <c>GET /scores</c>, which replaced the Report tab's canned SELECT against this table — the one
    /// report section that had no wire type and so kept the SQL console alive.</summary>
    IReadOnlyList<ScoreRow> QueryScores(string runId);

    // ---------------------------------------------------------------- ledger

    void WriteLedger(string runId, int? sessionNumber, string? stageId, string kind, string content);

    // ---------------------------------------------------------------- bugs (M7.2)

    long WriteBug(string runId, string title, string? detail, string severity, string? stageId, int? foundSession);
    IReadOnlyList<BugRow> QueryBugs(string runId, string? status = null);
    bool UpdateBugStatus(string runId, long bugId, string status, int? fixedSession);

    /// <summary>SF0.4: open bugs filed by EARLIER runs in this same run.db, so a bug outlives the run that
    /// found it and not just the session. See <see cref="CarriedBugRow"/>.</summary>
    IReadOnlyList<CarriedBugRow> QueryCarriedBugs(string currentRunId);

    // ---------------------------------------------------------------- handovers

    void WriteHandover(string runId, int sessionNumber, string stageId, string content);
    string? GetLatestHandover(string runId, string? stageId = null);

    // ---------------------------------------------------------------- injections

    void WriteInjection(string runId, string kind, int? sourceSession, string? targetStageId, string content);

    // ---------------------------------------------------------------- checkpoints (W1.1: graph views)

    // Since W1.1 these are adapters over the event-sourced work graph — writes emit task events,
    // reads fold the log; the mutable checkpoints table is gone (migration v8). `source` is the
    // claim provenance stamped on the emitted TaskStatusChanged (tracker | engine | agent | human).

    /// <summary>Returns the checkpoint's POST-FOLD status (SC5.3) — "in_progress" when the move landed,
    /// the card's real status when the todo-only rule refused it, "" when no such card exists.</summary>
    string MarkCheckpointInProgress(string runId, string checkpointId, string source = "agent");

    /// <summary>SC5.3: the shared status move every board ingress makes — validated by
    /// <see cref="TaskWrites.BuildStatusChange"/>, legality owned by the fold, and the POST-FOLD status
    /// returned so a caller reports what happened, not what it asked for. Error is set only for a
    /// malformed request (unknown id, unknown status); a transition the fold refuses comes back Ok with
    /// the card's unchanged status, exactly as <c>POST /tasks/update</c> answers.</summary>
    (string? Status, string? Error) ApplyTaskStatus(string runId, string taskId, string status,
        string? commit = null, string? evidence = null, string source = "agent");

    /// <summary>SC5.3: append a stamped acceptance correction to a card's context, returning the
    /// post-fold context. The correction reaches the next session through the composed prompt.</summary>
    (string? Context, string? Error) AmendTask(string runId, string taskId, string note);
    void ConfirmCheckpoints(string runId, IEnumerable<string> checkpointIds, int? sessionNumber = null);
    IReadOnlyList<CheckpointRow> GetCheckpoints(string runId);
    void SeedCheckpoints(string runId,
        IEnumerable<(string Id, string StageId, string Title, string Status, string Commit, string Evidence)> checkpoints);
    void UpdateCheckpoint(string runId, string checkpointId, string status, string commit, string evidence,
        string source = "engine");

    /// <summary>SC5.1: record a session's "cannot proceed until T" into the run's event log, from
    /// whichever process the agent called from. Routed like every other cross-process write
    /// (<see cref="MarkCheckpointInProgress"/>) and flushed before returning, so the engine reading
    /// the log at verdict time cannot miss a request the CLI already acknowledged.</summary>
    void RequestBlockedUntil(string runId, DateTimeOffset untilUtc, string reason, string? stageId,
        string source = "agent");

    // ---------------------------------------------------------------- pids

    void TrackPid(int pid, string runId, string purpose, string? stageId, int? sessionNumber, DateTime startedUtc);
    void MarkPidExited(int pid, int? exitCode);
    IReadOnlyList<OrphanPidRow> GetOrphanPids(string runId);
    IReadOnlyList<PidRow> GetAllPids(string runId);

    // ---------------------------------------------------------------- events (replaces events.jsonl)

    void AppendEvent(ConductorEvent evt);

    /// <summary>Synchronously persists any queued events (they are normally drained on a ~200ms
    /// cadence). Call after a write whose caller will immediately read the event log back — e.g. the
    /// control plane's task writes, where the Face re-fetches <c>GET /tasks</c> right away.</summary>
    void FlushEvents();
    IReadOnlyList<ConductorEvent> ReadAllEvents(string runId);
    IReadOnlyList<ConductorEvent> ReadEventsAfter(string runId, long afterSeq);

    RunStateProjection.InterruptedSessionInfo? FindInterruptedSession(string runId);

    // ---------------------------------------------------------------- run state (replaces state.json)

    string? GetLatestRunId(string planName);
    string? LoadRunStateJson(string runId);
    void SaveRunState(string runId, string planName, string stateJson);

    // ---------------------------------------------------------------- typed queries

    IReadOnlyList<LedgerRow> QueryLedger(string runId, string? stageId = null, string? kind = null);
    RunRow? QueryRun(string runId);
    IReadOnlyList<CostCategoryRow> QueryCostTotals(string runId);
    SessionDetailRow? QuerySessionByNumber(string runId, int number);
    IReadOnlyList<SessionSummaryRow> QuerySessions(string runId);
    IReadOnlyList<GateDetailRow> QueryGatesForSession(string runId, int sessionNumber);
    IReadOnlyList<StageOutcomeRow> QuerySessionOutcomesByStage(string runId);
    IReadOnlyList<GateFailureRow> QueryRecentGateFailures(string runId, int limit = 5);

    // ---------------------------------------------------------------- raw query (read-only, parametrized)

    IReadOnlyList<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] parameters);
}
