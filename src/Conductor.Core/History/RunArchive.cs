using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Models;
using Microsoft.Data.Sqlite;

namespace Conductor.Core.History;

/// <summary>
/// K3.2: one archived run database, opened <b>read-only</b>.
/// <para><b>Why not <c>SqliteRunStore</c>.</b> That type's constructor creates the parent directory,
/// sets <c>journal_mode=WAL</c> and runs <c>MigrationRunner</c> — three writes before the first read.
/// Pointing it at a run from July would rewrite that run's schema just to look at it, and a run this
/// engine can no longer open is exactly the run history exists to preserve. So browsing gets its own
/// door: <c>Mode=ReadOnly</c>, which makes SQLite itself refuse every write, and a type with no write
/// method on it at all. Read-only is enforced by the connection, not by discipline.</para>
/// <para>Timestamps stay the strings the schema stores, the way <c>SqliteRunStore.Queries</c> hands
/// them out — a browse must not fail because a row from an older engine spells a date differently.</para>
/// </summary>
public sealed class RunArchive
{
    private readonly string _connectionString;

    private RunArchive(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        // Mode=ReadOnly is the guarantee. Cache=Private keeps this reader out of any shared cache a
        // live engine in this process holds on the same file.
        _connectionString = $"Data Source={DbPath};Mode=ReadOnly;Cache=Private";
    }

    /// <summary>The database this archive reads.</summary>
    public string DbPath { get; }

    /// <summary>
    /// Points an archive at a run database, or returns null when it is missing, unreadable, or not a
    /// conductor database at all. Null rather than throwing because the caller is a listing: one bad
    /// entry must not take the catalogue down with it.
    /// <para>Each read opens and closes its own connection — an archive holds no handle open between
    /// calls, so browsing never becomes a reader that a live engine's writer has to wait behind.</para>
    /// </summary>
    public static RunArchive? TryOpen(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return null;
        var archive = new RunArchive(dbPath);
        return archive.IsRunDatabase() ? archive : null;
    }

    /// <summary>True when this file is a conductor run database this engine can read.</summary>
    public bool IsRunDatabase()
    {
        try
        {
            return Query("SELECT name FROM sqlite_master WHERE type='table' AND name='runs' LIMIT 1").Count > 0;
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a SELECT against the archive. Public on purpose: it is the one place blocking database
    /// I/O happens, it belongs on this synchronous boundary, and hiding it behind a private helper
    /// would only hide it from the analyzer. Safe by construction — the connection is
    /// <c>Mode=ReadOnly</c>, so SQLite rejects any statement that would write.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Query(
        string sql, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var rows = new List<Dictionary<string, object?>>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
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

    /// <summary>Every run in this database, newest first. A database holds more than one when a repo
    /// has been run repeatedly — which is the whole point of keeping it.</summary>
    public IReadOnlyList<ArchivedRun> Runs()
    {
        var rows = Query(
            "SELECT r.run_id, r.plan_name, r.repo, r.branch, r.driver_ver, r.status, " +
            "  r.started_utc, r.ended_utc, " +
            "  (SELECT COUNT(*) FROM sessions s WHERE s.run_id = r.run_id) AS session_count, " +
            "  (SELECT COALESCE(SUM(c.cost_usd), 0) FROM costs c WHERE c.run_id = r.run_id) AS cost_usd, " +
            "  (SELECT COALESCE(SUM(c.tokens_in + c.tokens_out + c.tokens_think + c.tokens_cache), 0) " +
            "     FROM costs c WHERE c.run_id = r.run_id) AS tokens, " +
            "  (SELECT MAX(COALESCE(s.ended_utc, s.started_utc)) FROM sessions s WHERE s.run_id = r.run_id) AS last_session_utc " +
            "FROM runs r ORDER BY COALESCE(r.started_utc, '') DESC");
        return rows.Select(MapRun).ToList();
    }

    /// <summary>Every session of one run, oldest first — the order it was lived in, which is the
    /// order a replay reads in.</summary>
    public IReadOnlyList<ArchivedSession> Sessions(string runId)
    {
        var rows = Query(
            "SELECT s.number, s.stage_id, s.kind, s.started_utc, s.ended_utc, s.outcome, s.attempt, " +
            "  s.resume_count, s.commit_count, s.result_summary, s.gate_summary, " +
            "  (SELECT COALESCE(SUM(c.cost_usd), 0) FROM costs c " +
            "     WHERE c.run_id = s.run_id AND c.session_number = s.number) AS cost_usd, " +
            "  (SELECT COALESCE(SUM(c.tokens_in + c.tokens_out + c.tokens_think + c.tokens_cache), 0) " +
            "     FROM costs c WHERE c.run_id = s.run_id AND c.session_number = s.number) AS tokens " +
            "FROM sessions s WHERE s.run_id = @runId ORDER BY s.number",
            ("@runId", runId));
        return rows.Select(MapSession).ToList();
    }

    /// <summary>The declared stages of one run, in the order the engine recorded them.</summary>
    public IReadOnlyList<ArchivedStage> Stages(string runId)
    {
        var rows = Query(
            "SELECT id, title, status, session_count, started_utc, confirmed_utc " +
            "FROM stages WHERE run_id = @runId ORDER BY COALESCE(started_utc, ''), id",
            ("@runId", runId));
        return rows.Select(MapStage).ToList();
    }

    /// <summary>
    /// The checkpoints of one run, folded out of the event log exactly the way the live store does
    /// it — the mutable <c>checkpoints</c> table was dropped in schema v8, so this fold IS the truth
    /// and re-deriving it here would be a second, divergent answer.
    /// </summary>
    public IReadOnlyList<ArchivedCheckpoint> Checkpoints(string runId)
    {
        var rows = Query(
            "SELECT type, payload FROM events WHERE run_id = @runId ORDER BY seq",
            ("@runId", runId));
        var events = new List<ConductorEvent>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                if (row["payload"] is not string json) continue;
                if (JsonSerializer.Deserialize<ConductorEvent>(json, PlanConfig.JsonOpts) is { } evt)
                    events.Add(evt);
            }
            catch (JsonException)
            {
                // Same tolerance as SqliteRunStore.DeserializeEvents: a torn event is skipped, not fatal.
            }
        }
        var graph = new TaskGraph();
        graph.Fold(events);
        return graph.Checkpoints()
            .Where(t => !string.Equals(t.Status, "archived", StringComparison.Ordinal))
            .Select(t => new ArchivedCheckpoint(t.TaskId, t.StageId ?? "", t.Title,
                TaskWrites.Label(t.Status), t.Commit, t.Evidence, t.Confirmed))
            .ToList();
    }

    private static ArchivedRun MapRun(Dictionary<string, object?> r) => new(
        RunId: (string)(r["run_id"] ?? "")!,
        PlanName: (string)(r["plan_name"] ?? "")!,
        Repo: (string)(r["repo"] ?? "")!,
        Branch: r["branch"] as string,
        EngineVersion: r["driver_ver"] as string,
        Status: (string)(r["status"] ?? "unknown")!,
        StartedUtc: r["started_utc"] as string,
        EndedUtc: r["ended_utc"] as string,
        LastActivityUtc: (r["last_session_utc"] as string) ?? (r["ended_utc"] as string) ?? (r["started_utc"] as string),
        Sessions: Convert.ToInt32(r["session_count"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        CostUsd: Convert.ToDecimal(r["cost_usd"] ?? 0m, System.Globalization.CultureInfo.InvariantCulture),
        Tokens: Convert.ToInt64(r["tokens"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture));

    private static ArchivedSession MapSession(Dictionary<string, object?> r) => new(
        Number: Convert.ToInt32(r["number"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        StageId: (string)(r["stage_id"] ?? "")!,
        Kind: (string)(r["kind"] ?? "")!,
        StartedUtc: r["started_utc"] as string,
        EndedUtc: r["ended_utc"] as string,
        Outcome: r["outcome"] as string,
        Attempt: Convert.ToInt32(r["attempt"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        ResumeCount: Convert.ToInt32(r["resume_count"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        Commits: Convert.ToInt32(r["commit_count"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        CostUsd: Convert.ToDecimal(r["cost_usd"] ?? 0m, System.Globalization.CultureInfo.InvariantCulture),
        Tokens: Convert.ToInt64(r["tokens"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
        ResultSummary: r["result_summary"] as string,
        GateSummary: r["gate_summary"] as string);

    private static ArchivedStage MapStage(Dictionary<string, object?> r) => new(
        Id: (string)(r["id"] ?? "")!,
        Title: (string)(r["title"] ?? "")!,
        Status: (string)(r["status"] ?? "")!,
        Sessions: Convert.ToInt32(r["session_count"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        StartedUtc: r["started_utc"] as string,
        ConfirmedUtc: r["confirmed_utc"] as string);
}

/// <summary>One run as the archive sees it. Timestamps are the raw stored strings.</summary>
public sealed record ArchivedRun(
    string RunId, string PlanName, string Repo, string? Branch, string? EngineVersion,
    string Status, string? StartedUtc, string? EndedUtc, string? LastActivityUtc,
    int Sessions, decimal CostUsd, long Tokens)
{
    /// <summary>First eight of the run id — the form every other surface prints.</summary>
    public string ShortRunId => RunId.Length >= 8 ? RunId[..8] : RunId;
}

/// <summary>One session of an archived run.</summary>
public sealed record ArchivedSession(
    int Number, string StageId, string Kind, string? StartedUtc, string? EndedUtc,
    string? Outcome, int Attempt, int ResumeCount, int Commits, decimal CostUsd, long Tokens,
    string? ResultSummary, string? GateSummary);

/// <summary>One declared stage of an archived run.</summary>
public sealed record ArchivedStage(
    string Id, string Title, string Status, int Sessions, string? StartedUtc, string? ConfirmedUtc);

/// <summary>One checkpoint, folded from the archived run's event log.</summary>
public sealed record ArchivedCheckpoint(
    string Id, string StageId, string Title, string Status, string? Commit, string? Evidence, bool Confirmed);
