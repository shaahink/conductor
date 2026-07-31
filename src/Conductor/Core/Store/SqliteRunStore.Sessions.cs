using System.Data;
using Conductor.Core.Events;

namespace Conductor.Core.Store;

public sealed partial class SqliteRunStore
{
    // ---------------------------------------------------------------- run lifecycle

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

    // ---------------------------------------------------------------- stage lifecycle

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

    // ---------------------------------------------------------------- session lifecycle

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

    // ---------------------------------------------------------------- costs

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

    // ---------------------------------------------------------------- gates

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

    public bool? GetLastPassingGateResult(string runId, string gateName, string tier, string sha)
    {
        if (_disposed != 0) return null;
        var rows = Query(
            """SELECT passed FROM gates WHERE run_id = @runId AND name = @name AND tier = @tier AND sha = @sha ORDER BY id DESC LIMIT 1""",
            ("@runId", runId), ("@name", gateName), ("@tier", tier), ("@sha", sha));
        if (rows.Count == 0) return null;
        return Convert.ToInt64(rows[0]["passed"]) != 0;
    }

    public long? GetLastPassingGateDurationMs(string runId, string gateName, string tier)
    {
        if (_disposed != 0) return null;
        var rows = Query(
            """
            SELECT duration_ms FROM gates
            WHERE run_id = @runId AND name = @name AND tier = @tier
              AND passed = 1 AND skipped = 0 AND duration_ms > 0
            ORDER BY id DESC LIMIT 1
            """,
            ("@runId", runId), ("@name", gateName), ("@tier", tier));
        if (rows.Count == 0) return null;
        return Convert.ToInt64(rows[0]["duration_ms"]);
    }

    // ---------------------------------------------------------------- scores

    public void WriteScore(string runId, int sessionNumber, string? stageId, int score, string verdict, string findings)
    {
        TryExecute(
            "INSERT INTO scores (run_id, session_number, stage_id, score, verdict, findings) " +
            "VALUES (@runId, @sessionNumber, @stageId, @score, @verdict, @findings)",
            ("@runId", runId), ("@sessionNumber", sessionNumber),
            ("@stageId", (object?)stageId ?? DBNull.Value),
            ("@score", score), ("@verdict", verdict), ("@findings", findings));
    }

    // ---------------------------------------------------------------- ledger

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

    // ---------------------------------------------------------------- handovers

    public void WriteHandover(string runId, int sessionNumber, string stageId, string content)
    {
        TryExecute(
            "INSERT INTO handovers (run_id, session_number, stage_id, content) " +
            "VALUES (@runId, @sessionNumber, @stageId, @content)",
            ("@runId", runId), ("@sessionNumber", sessionNumber),
            ("@stageId", stageId), ("@content", content));
    }

    public string? GetLatestHandover(string runId, string? stageId = null)
    {
        var sql = stageId != null
            ? "SELECT content FROM handovers WHERE run_id = @runId AND stage_id = @stageId ORDER BY id DESC LIMIT 1"
            : "SELECT content FROM handovers WHERE run_id = @runId ORDER BY id DESC LIMIT 1";
        var rows = stageId != null
            ? Query(sql, ("@runId", runId), ("@stageId", stageId))
            : Query(sql, ("@runId", runId));
        return rows.Count > 0 ? (string)rows[0]["content"]! : null;
    }

    // ---------------------------------------------------------------- injections

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

    // ---------------------------------------------------------------- checkpoints (W1.1: graph views)

    // The mutable checkpoints table is gone (v8) — these methods are thin adapters over the
    // event-sourced work graph: writes emit task events (flushed before returning, so a caller
    // that reads straight back sees them), reads fold the log. Signatures are unchanged, so every
    // caller — TrackerGenerator, the verdict engine, `conductor task` — moved onto the graph
    // without knowing it. ADR-0002's tracker-as-view now includes the checkpoint rows themselves.

    private TaskGraph FoldGraph(string runId)
    {
        var graph = new TaskGraph();
        graph.Fold(ReadAllEvents(runId));
        return graph;
    }

    /// <summary>Route a checkpoint write into the event log under the right run id. The engine sets
    /// the run id once (ConductorHost); the CLI claim path (`conductor task`) passes it explicitly
    /// and may target a different run than this store instance last stamped.</summary>
    private void EmitForRun(string runId, ConductorEvent evt)
    {
        if (!string.Equals(_runId, runId, StringComparison.Ordinal)) SetRunId(runId);
        Emit(evt);
    }

    public void MarkCheckpointInProgress(string runId, string checkpointId, string source = "agent")
    {
        // Parity with the SQL it replaces: TODO → IN PROGRESS only; never reopens a DONE row.
        var item = FoldGraph(runId).Find(checkpointId);
        if (item is not { Status: "todo" }) return;
        EmitForRun(runId, new TaskStatusChanged
        {
            RunId = runId, TaskId = checkpointId, Status = "in_progress", Source = source,
        });
        FlushEvents();
    }

    public IReadOnlyList<CheckpointRow> GetCheckpoints(string runId)
    {
        // Archived items (W1.2 retire) left the declared plan — they stay in the log, out of views.
        return FoldGraph(runId).Checkpoints().Where(t => t.Status != "archived").Select(t => new CheckpointRow(
            Id: t.TaskId,
            StageId: t.StageId,
            Title: t.Title,
            Status: CheckpointStatusLabel(t.Status),
            Commit: t.Commit,
            Evidence: t.Evidence,
            Confirmed: t.Confirmed
        )).ToList();
    }

    public void SeedCheckpoints(string runId,
        IEnumerable<(string Id, string StageId, string Title, string Status, string Commit, string Evidence)> checkpoints)
    {
        var graph = FoldGraph(runId);
        var order = graph.Count;
        foreach (var (id, stageId, title, status, commit, evidence) in checkpoints)
        {
            var existing = graph.Find(id);
            if (existing == null)
            {
                EmitForRun(runId, new TaskAdded
                {
                    RunId = runId, TaskId = id, CheckpointId = id, Title = title,
                    Source = "tracker", Order = order++,
                    Kind = WorkItemKinds.Checkpoint, StageId = stageId,
                });
                var graphStatus = GraphStatus(status);
                if (graphStatus != "todo")
                {
                    EmitForRun(runId, new TaskStatusChanged
                    {
                        RunId = runId, TaskId = id, Status = graphStatus,
                        Commit = Placeholder(commit), Evidence = Placeholder(evidence),
                        Source = "tracker",
                    });
                }
            }
            else if (!existing.Title.Equals(title, StringComparison.Ordinal))
            {
                // Parity with the old ON CONFLICT DO UPDATE SET title: declared titles refresh,
                // runtime status is never re-asserted for an item already in the graph — the
                // upsert-never-clobber principle (W1 design brief).
                EmitForRun(runId, new TaskDetailEdited { RunId = runId, TaskId = id, Title = title });
            }
        }
        FlushEvents();
    }

    public void UpdateCheckpoint(string runId, string checkpointId, string status, string commit, string evidence,
        string source = "engine")
    {
        if (FoldGraph(runId).Find(checkpointId) == null) return; // parity: UPDATE on a missing row was a no-op
        EmitForRun(runId, new TaskStatusChanged
        {
            RunId = runId, TaskId = checkpointId, Status = GraphStatus(status),
            Commit = Placeholder(commit), Evidence = Placeholder(evidence), Source = source,
        });
        FlushEvents();
    }

    public void ConfirmCheckpoints(string runId, IEnumerable<string> checkpointIds, int? sessionNumber = null)
    {
        var graph = FoldGraph(runId);
        foreach (var id in checkpointIds)
        {
            if (graph.Find(id) is not { } item) continue; // parity: confirming a missing row was a no-op
            EmitForRun(runId, new CheckpointConfirmed
            {
                RunId = runId, CheckpointId = id, StageId = item.StageId,
                SessionId = sessionNumber?.ToString(),
            });
        }
        FlushEvents();
    }

    /// <summary>Tracker-vocabulary status → graph status ("IN PROGRESS" → "in_progress", …).</summary>
    private static string GraphStatus(string status) => status.Trim().ToUpperInvariant() switch
    {
        "DONE" => "done",
        "IN PROGRESS" => "in_progress",
        "BLOCKED" => "blocked",
        "SKIPPED" => "skipped",
        "ARCHIVED" => "archived",
        _ => "todo",
    };

    /// <summary>Graph status → the tracker-vocabulary label the checkpoints table stored.</summary>
    private static string CheckpointStatusLabel(string status) => status switch
    {
        "done" => "DONE",
        "in_progress" => "IN PROGRESS",
        "blocked" => "BLOCKED",
        "skipped" => "SKIPPED",
        "archived" => "ARCHIVED",
        _ => "TODO",
    };

    /// <summary>The tracker's "-" placeholder means "nothing to record" — keep it out of the event
    /// so the fold's own defaults hold and replayed logs stay lean.</summary>
    private static string? Placeholder(string value) =>
        string.IsNullOrWhiteSpace(value) || value == "-" ? null : value;
}
