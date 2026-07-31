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

    public RunRow? QueryRun(string runId)
    {
        var rows = Query(
            "SELECT run_id, plan_name, repo, branch, driver_ver, status, started_utc, ended_utc " +
            "FROM runs WHERE run_id = @runId",
            ("@runId", runId));
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new RunRow(
            RunId: (string)r["run_id"]!,
            PlanName: (string)r["plan_name"]!,
            Repo: (string)r["repo"]!,
            Branch: r["branch"] as string,
            DriverVersion: r["driver_ver"] as string,
            Status: (string)(r["status"] ?? "unknown")!,
            StartedUtc: (string)(r["started_utc"] ?? "")!,
            EndedUtc: r["ended_utc"] as string);
    }

    public IReadOnlyList<CostCategoryRow> QueryCostTotals(string runId)
    {
        var rows = Query(
            "SELECT category, COALESCE(SUM(cost_usd), 0) AS cost_usd, " +
            "COALESCE(SUM(tokens_in + tokens_out + tokens_think + tokens_cache), 0) AS tokens " +
            "FROM costs WHERE run_id = @runId GROUP BY category ORDER BY category",
            ("@runId", runId));
        return rows.Select(r => new CostCategoryRow(
            Category: (string)(r["category"] ?? "unknown")!,
            CostUsd: Convert.ToDecimal(r["cost_usd"]),
            Tokens: Convert.ToInt64(r["tokens"]))).ToList();
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
        // The cost/token columns come from `costs`, which holds MANY rows per session (one per
        // category: agent | gate | advisor). Correlated SUM subqueries, never a JOIN — joining
        // multiplies the session row by its cost rows and every per-session figure comes out wrong.
        var rows = Query(
            "SELECT s.number, s.stage_id, s.kind, s.started_utc, s.ended_utc, s.outcome, s.attempt, " +
            "s.resume_count, s.gate_summary, s.result_summary, s.commit_count, " +
            "(SELECT COALESCE(SUM(c.cost_usd), 0) FROM costs c " +
            " WHERE c.run_id = s.run_id AND c.session_number = s.number) AS cost_usd, " +
            "(SELECT COALESCE(SUM(c.tokens_in), 0) FROM costs c " +
            " WHERE c.run_id = s.run_id AND c.session_number = s.number) AS tokens_in, " +
            "(SELECT COALESCE(SUM(c.tokens_out), 0) FROM costs c " +
            " WHERE c.run_id = s.run_id AND c.session_number = s.number) AS tokens_out, " +
            "(SELECT COALESCE(SUM(c.tokens_think), 0) FROM costs c " +
            " WHERE c.run_id = s.run_id AND c.session_number = s.number) AS tokens_think, " +
            "(SELECT COALESCE(SUM(c.tokens_cache), 0) FROM costs c " +
            " WHERE c.run_id = s.run_id AND c.session_number = s.number) AS tokens_cache " +
            "FROM sessions s WHERE s.run_id = @runId ORDER BY s.number DESC",
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
            CommitCount: Convert.ToInt32(r["commit_count"]),
            CostUsd: Convert.ToDouble(r["cost_usd"]),
            TokensIn: Convert.ToInt64(r["tokens_in"]),
            TokensOut: Convert.ToInt64(r["tokens_out"]),
            TokensThink: Convert.ToInt64(r["tokens_think"]),
            TokensCache: Convert.ToInt64(r["tokens_cache"])
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
