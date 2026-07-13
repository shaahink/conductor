using System.Data;

namespace Conductor.Core.Store;

public sealed partial class SqliteRunStore
{
    // ---------------------------------------------------------------- typed read queries

    public IReadOnlyList<LedgerRow> QueryLedger(string runId, string? stageId = null, string? kind = null)
    {
        var sql = "SELECT id, run_id, session_number, stage_id, kind, content, created_at " +
                  "FROM ledger WHERE run_id = @runId";
        var parameters = new List<(string, object?)> { ("@runId", runId) };

        if (stageId != null)
        {
            sql += " AND stage_id = @stageId";
            parameters.Add(("@stageId", stageId));
        }
        if (kind != null)
        {
            sql += " AND kind = @kind";
            parameters.Add(("@kind", kind));
        }
        sql += " ORDER BY id DESC";

        var rows = Query(sql, parameters.ToArray());
        return rows.Select(r => new LedgerRow(
            Id: Convert.ToInt64(r["id"]),
            RunId: (string)r["run_id"]!,
            SessionNumber: r["session_number"] is long sn ? (int?)sn : null,
            StageId: r["stage_id"] as string,
            Kind: (string)r["kind"]!,
            Content: (string)r["content"]!,
            CreatedAt: (string)(r["created_at"] ?? "")!
        )).ToList();
    }

    public SessionDetailRow? QuerySessionByNumber(string runId, int number)
    {
        var rows = Query(
            "SELECT number, stage_id, kind, started_utc, ended_utc, outcome, agent_session_id, " +
            "resume_count, attempt, gate_summary, result_summary, commit_count, newly_done " +
            "FROM sessions WHERE run_id = @runId AND number = @num",
            ("@runId", runId), ("@num", number));

        if (rows.Count == 0) return null;
        var r = rows[0];
        return new SessionDetailRow(
            Number: Convert.ToInt32(r["number"]),
            StageId: (string)r["stage_id"]!,
            Kind: (string)r["kind"]!,
            StartedUtc: r["started_utc"] as string,
            EndedUtc: r["ended_utc"] as string,
            Outcome: r["outcome"] as string,
            AgentSessionId: r["agent_session_id"] as string,
            ResumeCount: Convert.ToInt32(r["resume_count"]),
            Attempt: Convert.ToInt32(r["attempt"]),
            GateSummary: r["gate_summary"] as string,
            ResultSummary: r["result_summary"] as string,
            CommitCount: Convert.ToInt32(r["commit_count"]),
            NewlyDone: r["newly_done"] as string
        );
    }

    public IReadOnlyList<SessionSummaryRow> QuerySessions(string runId)
    {
        var rows = Query(
            "SELECT number, stage_id, kind, started_utc, ended_utc, outcome, attempt, " +
            "resume_count, gate_summary, result_summary, commit_count " +
            "FROM sessions WHERE run_id = @runId ORDER BY number DESC",
            ("@runId", runId));

        return rows.Select(r => new SessionSummaryRow(
            Number: Convert.ToInt32(r["number"]),
            StageId: (string)r["stage_id"]!,
            Kind: (string)r["kind"]!,
            StartedUtc: r["started_utc"] as string,
            EndedUtc: r["ended_utc"] as string,
            Outcome: r["outcome"] as string,
            Attempt: Convert.ToInt32(r["attempt"]),
            ResumeCount: Convert.ToInt32(r["resume_count"]),
            GateSummary: r["gate_summary"] as string,
            ResultSummary: r["result_summary"] as string,
            CommitCount: Convert.ToInt32(r["commit_count"])
        )).ToList();
    }

    public IReadOnlyList<GateDetailRow> QueryGatesForSession(
        string runId, int sessionNumber)
    {
        var rows = Query(
            "SELECT name, tier, passed, skipped, scope " +
            "FROM gates WHERE run_id = @runId AND session_number = @num ORDER BY id",
            ("@runId", runId), ("@num", sessionNumber));

        return rows.Select(r => new GateDetailRow(
            Name: (string)r["name"]!,
            Tier: r["tier"] as string,
            Passed: Convert.ToInt64(r["passed"]) != 0,
            Skipped: Convert.ToInt64(r["skipped"]) != 0,
            Scope: r["scope"] as string
        )).ToList();
    }

    public IReadOnlyList<StageOutcomeRow> QuerySessionOutcomesByStage(string runId)
    {
        var rows = Query(
            "SELECT stage_id, outcome, COUNT(*) as cnt FROM sessions " +
            "WHERE run_id = @runId GROUP BY stage_id, outcome ORDER BY stage_id",
            ("@runId", runId));

        return rows.Select(r => new StageOutcomeRow(
            StageId: (string)r["stage_id"]!,
            Outcome: (string)(r["outcome"] ?? "Unknown")!,
            Count: Convert.ToInt32(r["cnt"])
        )).ToList();
    }

    public IReadOnlyList<GateFailureRow> QueryRecentGateFailures(string runId, int limit = 5)
    {
        var rows = Query(
            "SELECT name, stage_id, tier FROM gates " +
            "WHERE run_id = @runId AND passed = 0 AND skipped = 0 " +
            "ORDER BY id DESC LIMIT @limit",
            ("@runId", runId), ("@limit", limit));

        return rows.Select(r => new GateFailureRow(
            Name: (string)r["name"]!,
            StageId: r["stage_id"] as string,
            Tier: (string)(r["tier"] ?? "full")!
        )).ToList();
    }
}
