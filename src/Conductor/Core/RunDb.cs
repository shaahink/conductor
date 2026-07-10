using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

/// <summary>
/// SQLite task store (<c>.conductor/run.db</c>) per AD-3 / D8. Owns the connection, creates
/// schema on open (idempotent), and provides write methods called alongside the existing
/// <c>state.json</c> and <c>events.jsonl</c> writes (additive-first — resumability never regresses).
/// Every write is best-effort: a <c>SqliteException</c> is logged and swallowed, never thrown.
/// Thread-safe: the connection runs in serialized mode.
/// </summary>
/// <remarks>
/// Schema version is stored in the <c>schema_version</c> table and checked on open; missing tables
/// are created. This is a simple hand-rolled migration — no heavy framework needed for a single-
/// writer, local SQLite database.
/// </remarks>
#pragma warning disable MA0045 // Schema creation + write methods are synchronous by design — the DB is a local file, and this is called during construction (no async ctor) and from sync event emission paths. The Orchestrator follows the same pattern for its file I/O.
public sealed class RunDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<RunDb> _logger;
    private readonly TimeProvider _clock;
    private bool _initialized;

    public static readonly int CurrentSchemaVersion = 1;

    public RunDb(string path, ILogger<RunDb> logger, TimeProvider? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        EnsureSchema();
    }

    public string ConnectionString => _conn.DataSource;

    private void EnsureSchema()
    {
        if (_initialized) return;
        using var tx = _conn.BeginTransaction();
        try
        {
            CreateSchema(_conn);
            tx.Commit();
            _initialized = true;
        }
        catch (SqliteException)
        {
            tx.Rollback();
            throw;
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Check existing version — if rows exist and version matches, skip.
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var existing = (long)cmd.ExecuteScalar()!;
        if (existing > 0)
        {
            cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
            var ver = (long)cmd.ExecuteScalar()!;
            if (ver != CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"run.db schema version mismatch: expected {CurrentSchemaVersion}, got {ver}. " +
                    "Delete .conductor/run.db and re-run, or migrate manually.");
            return;
        }

        cmd.CommandText = $"INSERT INTO schema_version (version) VALUES ({CurrentSchemaVersion});";
        cmd.ExecuteNonQuery();

        // --- runs (one row per logical run) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS runs (
                run_id       TEXT PRIMARY KEY,
                plan_name    TEXT NOT NULL,
                repo         TEXT NOT NULL,
                branch       TEXT,
                driver_ver   TEXT,
                status       TEXT NOT NULL DEFAULT 'running',
                started_utc  TEXT NOT NULL,
                ended_utc    TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // --- stages (one row per stage per run) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS stages (
                id            TEXT NOT NULL,
                run_id        TEXT NOT NULL,
                title         TEXT NOT NULL,
                status        TEXT NOT NULL DEFAULT 'pending',
                session_count INTEGER NOT NULL DEFAULT 0,
                started_utc   TEXT,
                confirmed_utc TEXT,
                PRIMARY KEY (id, run_id),
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- sessions (one row per agent invocation) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                stage_id        TEXT NOT NULL,
                number          INTEGER NOT NULL,
                kind            TEXT NOT NULL,
                started_utc     TEXT NOT NULL,
                ended_utc       TEXT,
                outcome         TEXT,
                agent_session_id TEXT,
                resume_count    INTEGER NOT NULL DEFAULT 0,
                attempt         INTEGER NOT NULL DEFAULT 0,
                gate_summary    TEXT,
                result_summary  TEXT,
                commit_count    INTEGER NOT NULL DEFAULT 0,
                newly_done      TEXT,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- attempts (per-stage attempt counter) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS attempts (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                stage_id        TEXT NOT NULL,
                number          INTEGER NOT NULL,
                session_number  INTEGER NOT NULL,
                started_utc     TEXT NOT NULL,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- gates (per-gate invocation) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gates (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                session_number  INTEGER,
                stage_id        TEXT,
                name            TEXT NOT NULL,
                tier            TEXT NOT NULL DEFAULT 'full',
                scope           TEXT NOT NULL DEFAULT 'session',
                sha             TEXT,
                passed          INTEGER NOT NULL,
                skipped         INTEGER NOT NULL DEFAULT 0,
                optional        INTEGER NOT NULL DEFAULT 0,
                exit_code       INTEGER NOT NULL,
                duration_ms     INTEGER NOT NULL,
                tail            TEXT,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- scores (from Verifier, F4) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scores (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                session_number  INTEGER NOT NULL,
                stage_id        TEXT,
                score           INTEGER NOT NULL,
                verdict         TEXT,
                findings        TEXT,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- ledger (knowledge ledger, append-only) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ledger (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                session_number  INTEGER,
                stage_id        TEXT,
                kind            TEXT NOT NULL,
                content         TEXT NOT NULL,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- handovers (structured from handoff blocks) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS handovers (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                session_number  INTEGER NOT NULL,
                stage_id        TEXT NOT NULL,
                content         TEXT NOT NULL,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- injections (human/advisor/verifier/auto) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS injections (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                kind            TEXT NOT NULL,
                source_session  INTEGER,
                target_stage_id TEXT,
                content         TEXT NOT NULL,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- costs (per-session cost breakdown, D8) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS costs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id          TEXT NOT NULL,
                session_number  INTEGER NOT NULL,
                category        TEXT NOT NULL,
                tokens_in       INTEGER NOT NULL DEFAULT 0,
                tokens_out      INTEGER NOT NULL DEFAULT 0,
                tokens_think    INTEGER NOT NULL DEFAULT 0,
                tokens_cache    INTEGER NOT NULL DEFAULT 0,
                cost_usd        REAL NOT NULL DEFAULT 0,
                wall_ms         INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

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

    // ---------------------------------------------------------------- query (F1.4 surface)

    /// <summary>
    /// Execute a raw SQL query and return the results as a list of dictionaries
    /// (column name → value). Used by <c>conductor report --query</c> (F1.4). Accepts
    /// parameterised queries to prevent injection.
    /// </summary>
    public List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    // ---------------------------------------------------------------- helpers

    private void TryExecute(string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "run.db write failed (additive, non-blocking): {Sql}", sql);
        }
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
#pragma warning restore MA0045
}
