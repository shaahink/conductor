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

    void InitializeRun(string runId, string planName, string repo, string? branch, string? driverVersion);
    void RecordRunEnd(string runId, string status);

    // ---------------------------------------------------------------- stage lifecycle

    void InitializeStage(string runId, string stageId, string title);
    void ConfirmStage(string runId, string stageId);

    // ---------------------------------------------------------------- session lifecycle

    void RecordSession(
        string runId, string stageId, int number, string kind,
        DateTime startedUtc, DateTime? endedUtc, string? outcome,
        string? agentSessionId, int resumeCount, int attempt,
        string? gateSummary, string? resultSummary, int commitCount, string? newlyDone);

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

    // ---------------------------------------------------------------- scores

    void WriteScore(string runId, int sessionNumber, string? stageId, int score, string verdict, string findings);

    // ---------------------------------------------------------------- ledger

    void WriteLedger(string runId, int? sessionNumber, string? stageId, string kind, string content);

    // ---------------------------------------------------------------- handovers

    void WriteHandover(string runId, int sessionNumber, string stageId, string content);
    string? GetLatestHandover(string runId, string? stageId = null);

    // ---------------------------------------------------------------- injections

    void WriteInjection(string runId, string kind, int? sourceSession, string? targetStageId, string content);

    // ---------------------------------------------------------------- checkpoints

    void MarkCheckpointInProgress(string runId, string checkpointId);
    void ConfirmCheckpoints(string runId, IEnumerable<string> checkpointIds);
    IReadOnlyList<CheckpointRow> GetCheckpoints(string runId);
    void SeedCheckpoints(string runId,
        IEnumerable<(string Id, string StageId, string Title, string Status, string Commit, string Evidence)> checkpoints);
    void UpdateCheckpoint(string runId, string checkpointId, string status, string commit, string evidence);

    // ---------------------------------------------------------------- pids

    void TrackPid(int pid, string runId, string purpose, string? stageId, int? sessionNumber, DateTime startedUtc);
    void MarkPidExited(int pid, int? exitCode);
    IReadOnlyList<OrphanPidRow> GetOrphanPids(string runId);
    IReadOnlyList<PidRow> GetAllPids(string runId);

    // ---------------------------------------------------------------- events (replaces events.jsonl)

    void AppendEvent(ConductorEvent evt);
    IReadOnlyList<ConductorEvent> ReadAllEvents(string runId);
    IReadOnlyList<ConductorEvent> ReadEventsAfter(string runId, long afterSeq);

    RunStateProjection.InterruptedSessionInfo? FindInterruptedSession(string runId);

    // ---------------------------------------------------------------- run state (replaces state.json)

    string? GetLatestRunId(string planName);
    string? LoadRunStateJson(string runId);
    void SaveRunState(string runId, string planName, string stateJson);

    // ---------------------------------------------------------------- typed queries

    IReadOnlyList<LedgerRow> QueryLedger(string runId, string? stageId = null, string? kind = null);
    SessionDetailRow? QuerySessionByNumber(string runId, int number);
    IReadOnlyList<SessionSummaryRow> QuerySessions(string runId);
    IReadOnlyList<GateDetailRow> QueryGatesForSession(string runId, int sessionNumber);
    IReadOnlyList<StageOutcomeRow> QuerySessionOutcomesByStage(string runId);
    IReadOnlyList<GateFailureRow> QueryRecentGateFailures(string runId, int limit = 5);

    // ---------------------------------------------------------------- raw query (read-only, parametrized)

    IReadOnlyList<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] parameters);
}
