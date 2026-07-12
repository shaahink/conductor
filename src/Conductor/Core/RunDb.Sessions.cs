using System.Data;
using Microsoft.Data.Sqlite;

namespace Conductor.Core;

public partial class RunDb
{
    // ---------------------------------------------------------------- write methods

    public void InitializeRun(string runId, string planName, string repo, string? branch, string? driverVersion)
    {
        TryExecute("INSERT OR REPLACE INTO runs (run_id, plan_name, repo, branch, driver_ver, status, started_utc) " +
                   "VALUES (@runId, @planName, @repo, @branch, @driverVer, 'running', @now)",
                   ("@runId", runId), ("@planName", planName), ("@repo", repo),
                   ("@branch", (object?)branch ?? DBNull.Value),
                   ("@driverVer", (object?)driverVersion ?? DBNull.Value),
                   ("@now", _clock.GetUtcNow().ToString("O")));
    }

    public void RecordRunEnd(string runId, string status)
    {
        TryExecute("UPDATE runs SET status = @status, ended_utc = @now WHERE run_id = @runId",
                   ("@runId", runId), ("@status", status),
                   ("@now", _clock.GetUtcNow().ToString("O")));
    }

    public void InitializeStage(string runId, string stageId, string title)
    {
        TryExecute("INSERT OR REPLACE INTO stages (id, run_id, title, status, started_utc) " +
                   "VALUES (@id, @runId, @title, 'in_progress', @now)",
                   ("@id", stageId), ("@runId", runId), ("@title", title),
                   ("@now", _clock.GetUtcNow().ToString("O")));
    }

    public void ConfirmStage(string runId, string stageId)
    {
        TryExecute("UPDATE stages SET status = 'done', confirmed_utc = @now WHERE id = @id AND run_id = @runId",
                   ("@id", stageId), ("@runId", runId),
                   ("@now", _clock.GetUtcNow().ToString("O")));
    }

    public void RecordSession(
        string runId, string stageId, int number, string kind,
        DateTime startedUtc, DateTime? endedUtc, string? outcome,
        string? agentSessionId, int resumeCount, int attempt,
        string? gateSummary, string? resultSummary, int commitCount, string? newlyDone)
    {
        TryExecute(
            "INSERT INTO sessions (run_id, stage_id, number, kind, started_utc, ended_utc, outcome, " +
            "agent_session_id, resume_count, attempt, gate_summary, result_summary, commit_count, newly_done) " +
            "VALUES (@runId, @stageId, @number, @kind, @started, @ended, @outcome, " +
            "@agentSessionId, @resumeCount, @attempt, @gateSummary, @resultSummary, @commitCount, @newlyDone)",
            ("@runId", runId), ("@stageId", stageId), ("@number", number), ("@kind", kind),
            ("@started", startedUtc.ToString("O")),
            ("@ended", (object?)(endedUtc?.ToString("O")) ?? DBNull.Value),
            ("@outcome", (object?)outcome ?? DBNull.Value),
            ("@agentSessionId", (object?)agentSessionId ?? DBNull.Value),
            ("@resumeCount", resumeCount), ("@attempt", attempt),
            ("@gateSummary", (object?)gateSummary ?? DBNull.Value),
            ("@resultSummary", (object?)resultSummary ?? DBNull.Value),
            ("@commitCount", commitCount),
            ("@newlyDone", (object?)newlyDone ?? DBNull.Value));
    }

    public void RecordAttempt(string runId, string stageId, int number, int sessionNumber, DateTime startedUtc)
    {
        TryExecute(
            "INSERT INTO attempts (run_id, stage_id, number, session_number, started_utc) " +
            "VALUES (@runId, @stageId, @number, @sessionNumber, @started)",
            ("@runId", runId), ("@stageId", stageId), ("@number", number),
            ("@sessionNumber", sessionNumber),
            ("@started", startedUtc.ToString("O")));
    }

    public void RecordGate(
        string runId, int? sessionNumber, string? stageId,
        string name, string tier, string scope, string? sha,
        bool passed, bool skipped, bool optional, int exitCode, long durationMs, string? tail)
    {
        TryExecute(
            "INSERT INTO gates (run_id, session_number, stage_id, name, tier, scope, sha, " +
            "passed, skipped, optional, exit_code, duration_ms, tail) " +
            "VALUES (@runId, @sessionNumber, @stageId, @name, @tier, @scope, @sha, " +
            "@passed, @skipped, @optional, @exitCode, @durationMs, @tail)",
            ("@runId", runId),
            ("@sessionNumber", (object?)sessionNumber ?? DBNull.Value),
            ("@stageId", (object?)stageId ?? DBNull.Value),
            ("@name", name), ("@tier", tier), ("@scope", scope),
            ("@sha", (object?)sha ?? DBNull.Value),
            ("@passed", passed ? 1 : 0),
            ("@skipped", skipped ? 1 : 0),
            ("@optional", optional ? 1 : 0),
            ("@exitCode", exitCode),
            ("@durationMs", durationMs),
            ("@tail", (object?)tail ?? DBNull.Value));
    }

    public void RecordCost(
        string runId, int sessionNumber, string category,
        long tokensIn, long tokensOut, long tokensThink, long tokensCache,
        decimal costUsd, long wallMs)
    {
        TryExecute(
            "INSERT INTO costs (run_id, session_number, category, tokens_in, tokens_out, " +
            "tokens_think, tokens_cache, cost_usd, wall_ms) " +
            "VALUES (@runId, @sessionNumber, @category, @tokensIn, @tokensOut, " +
            "@tokensThink, @tokensCache, @costUsd, @wallMs)",
            ("@runId", runId), ("@sessionNumber", sessionNumber), ("@category", category),
            ("@tokensIn", tokensIn), ("@tokensOut", tokensOut),
            ("@tokensThink", tokensThink), ("@tokensCache", tokensCache),
            ("@costUsd", costUsd), ("@wallMs", wallMs));
    }

    public void WriteLedger(string runId, int? sessionNumber, string? stageId, string kind, string content)
    {
        TryExecute(
            "INSERT INTO ledger (run_id, session_number, stage_id, kind, content) " +
            "VALUES (@runId, @sessionNumber, @stageId, @kind, @content)",
            ("@runId", runId),
            ("@sessionNumber", (object?)sessionNumber ?? DBNull.Value),
            ("@stageId", (object?)stageId ?? DBNull.Value),
            ("@kind", kind), ("@content", content));
    }

    public void WriteHandover(string runId, int sessionNumber, string stageId, string content)
    {
        TryExecute(
            "INSERT INTO handovers (run_id, session_number, stage_id, content) " +
            "VALUES (@runId, @sessionNumber, @stageId, @content)",
            ("@runId", runId), ("@sessionNumber", sessionNumber),
            ("@stageId", stageId), ("@content", content));
    }

    public void WriteInjection(string runId, string kind, int? sourceSession, string? targetStageId, string content)
    {
        TryExecute(
            "INSERT INTO injections (run_id, kind, source_session, target_stage_id, content) " +
            "VALUES (@runId, @kind, @sourceSession, @targetStageId, @content)",
            ("@runId", runId), ("@kind", kind),
            ("@sourceSession", (object?)sourceSession ?? DBNull.Value),
            ("@targetStageId", (object?)targetStageId ?? DBNull.Value),
            ("@content", content));
    }

    public void WriteScore(string runId, int sessionNumber, string? stageId, int score, string verdict, string findings)
    {
        TryExecute(
            "INSERT INTO scores (run_id, session_number, stage_id, score, verdict, findings) " +
            "VALUES (@runId, @sessionNumber, @stageId, @score, @verdict, @findings)",
            ("@runId", runId), ("@sessionNumber", sessionNumber),
            ("@stageId", (object?)stageId ?? DBNull.Value),
            ("@score", score), ("@verdict", verdict), ("@findings", findings));
    }

    // ---------------------------------------------------------------- checkpoints (F1.2)

    /// <summary>Seed checkpoints from the plan. Each call is an UPSERT — re-seeding on
    /// resume does not lose status previously written by <see cref="UpdateCheckpoint"/>.</summary>
    public void SeedCheckpoints(string runId,
        IEnumerable<(string Id, string StageId, string Title, string Status, string Commit, string Evidence)> checkpoints)
    {
        foreach (var (id, stageId, title, status, commit, evidence) in checkpoints)
        {
            TryExecute(
                "INSERT INTO checkpoints (id, run_id, stage_id, title, status, \"commit\", evidence) " +
                "VALUES (@id, @runId, @stageId, @title, @status, @commit, @evidence) " +
                "ON CONFLICT(id, run_id) DO UPDATE SET title = excluded.title, stage_id = excluded.stage_id;",
                ("@id", id), ("@runId", runId), ("@stageId", stageId),
                ("@title", title), ("@status", status), ("@commit", commit), ("@evidence", evidence));
        }
    }

    /// <summary>Mark a checkpoint as DONE with the attributed commit and evidence string.</summary>
    public void UpdateCheckpoint(string runId, string checkpointId, string status, string commit, string evidence)
    {
        TryExecute(
            "UPDATE checkpoints SET status = @status, \"commit\" = @commit, evidence = @evidence " +
            "WHERE id = @id AND run_id = @runId",
            ("@runId", runId), ("@id", checkpointId),
            ("@status", status), ("@commit", commit), ("@evidence", evidence));
    }

    // ---------------------------------------------------------------- F7.4: per-gate SHA cache

    /// <summary>Returns true if the gate with the given name, tier and SHA has a passing result
    /// recorded in run.db. Returns null if no matching record exists (e.g. first run, changed SHA).
    /// The cache is per-run to avoid cross-contamination.</summary>
    public bool? GetLastPassingGateResult(string runId, string gateName, string tier, string sha)
    {
        if (_disposed != 0) return null;
        var rows = Query(
            """SELECT passed FROM gates WHERE run_id = @runId AND name = @name AND tier = @tier AND sha = @sha ORDER BY id DESC LIMIT 1""",
            ("@runId", runId), ("@name", gateName), ("@tier", tier), ("@sha", sha));
        if (rows.Count == 0) return null;
        return Convert.ToInt64(rows[0]["passed"]) != 0;
    }
}
