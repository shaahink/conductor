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
public sealed partial class RunDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<RunDb> _logger;
    private readonly TimeProvider _clock;
    private bool _initialized;

    public static readonly int CurrentSchemaVersion = 4;

    public RunDb(string path, ILogger<RunDb> logger, TimeProvider? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path}");
        try
        {
            _conn.Open();
            using var pragma = _conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
            EnsureSchema();
        }
        catch
        {
            _conn.Close();
            _conn.Dispose();
            throw;
        }
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
            if (ver > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"run.db schema version is newer ({ver}) than supported ({CurrentSchemaVersion}). " +
                    "Use a newer Conductor build.");
            if (ver == CurrentSchemaVersion)
                return;
            // Migrate from older version inside this transaction
            MigrateFrom(conn, (int)ver);
            cmd.CommandText = $"UPDATE schema_version SET version = {CurrentSchemaVersion};";
            cmd.ExecuteNonQuery();
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
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
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

        // --- checkpoints (F1.2: tracker-as-view — checkpoint definitions + status from run.db) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS checkpoints (
                id              TEXT NOT NULL,
                run_id          TEXT NOT NULL,
                stage_id        TEXT NOT NULL,
                title           TEXT NOT NULL,
                status          TEXT NOT NULL DEFAULT 'TODO',
                "commit"        TEXT NOT NULL DEFAULT '-',
                evidence        TEXT NOT NULL DEFAULT '-',
                PRIMARY KEY (id, run_id),
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // --- pids (F2.2: process tracking for orphan reaper + liveness) ---
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pids (
                pid             INTEGER NOT NULL,
                purpose         TEXT NOT NULL,
                stage_id        TEXT,
                session_number  INTEGER,
                started_utc     TEXT NOT NULL,
                exited_utc      TEXT,
                exit_code       INTEGER,
                run_id          TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Migrate from an older schema version. Called inside the EnsureSchema transaction.</summary>
    private static void MigrateFrom(SqliteConnection conn, int fromVersion)
    {
        if (fromVersion < 2)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS checkpoints (
                    id              TEXT NOT NULL,
                    run_id          TEXT NOT NULL,
                    stage_id        TEXT NOT NULL,
                    title           TEXT NOT NULL,
                    status          TEXT NOT NULL DEFAULT 'TODO',
                    "commit"        TEXT NOT NULL DEFAULT '-',
                    evidence        TEXT NOT NULL DEFAULT '-',
                    PRIMARY KEY (id, run_id),
                    FOREIGN KEY (run_id) REFERENCES runs(run_id)
                );
                """;
            cmd.ExecuteNonQuery();
        }
        if (fromVersion < 3)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS pids (
                    pid             INTEGER NOT NULL,
                    purpose         TEXT NOT NULL,
                    stage_id        TEXT,
                    session_number  INTEGER,
                    started_utc     TEXT NOT NULL,
                    exited_utc      TEXT,
                    exit_code       INTEGER,
                    run_id          TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        if (fromVersion < 4)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE ledger ADD COLUMN created_at TEXT NOT NULL DEFAULT (datetime('now'));
                """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Set a checkpoint to IN PROGRESS.</summary>
    public void MarkCheckpointInProgress(string runId, string checkpointId)
    {
        TryExecute(
            "UPDATE checkpoints SET status = 'IN PROGRESS' WHERE id = @id AND run_id = @runId AND status = 'TODO'",
            ("@runId", runId), ("@id", checkpointId));
    }

    /// <summary>Return all checkpoint rows for a run, ordered by stage then id.</summary>
    public IReadOnlyList<(string Id, string StageId, string Title, string Status, string Commit, string Evidence)>
        GetCheckpoints(string runId)
    {
        var rows = Query(
            "SELECT id, stage_id, title, status, \"commit\", evidence FROM checkpoints " +
            "WHERE run_id = @runId ORDER BY stage_id, id",
            ("@runId", runId));
        return rows.Select(r => (Id: (string)r["id"]!, StageId: (string)r["stage_id"]!,
            Title: (string)r["title"]!, Status: (string)r["status"]!,
            Commit: (string)(r["commit"] ?? "-")!, Evidence: (string)(r["evidence"] ?? "-")!)).ToList();
    }

    /// <summary>Return the most recent handover content for a stage (or the latest overall).</summary>
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

    // ---------------------------------------------------------------- query (F1.4 surface)

    /// <summary>
    /// Execute a raw SQL query and return the results as a list of dictionaries
    /// (column name → value). Used by <c>conductor report --query</c> (F1.4). Accepts
    /// parameterised queries to prevent injection.
    /// </summary>
    public List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(RunDb));
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
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "run.db write failed (additive, non-blocking): {Sql}", sql);
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        try { _conn.Close(); } catch { }
        try { _conn.Dispose(); } catch { }
    }
#pragma warning restore MA0045
}

/// <summary>F2.3: A row from the pids table returned by <see cref="RunDb.GetAllPids"/>.</summary>
public sealed record PidRow(
    int Pid,
    string Purpose,
    string? StageId,
    int? SessionNumber,
    DateTime StartedUtc,
    DateTime? ExitedUtc,
    int? ExitCode,
    string RunId);
