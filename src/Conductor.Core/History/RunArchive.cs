using Conductor.Core.Events;
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
public sealed partial class RunArchive
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

    /// <summary>
    /// The columns a table actually has, cached per archive. An archive opens databases this engine
    /// did not write — a v9 import, a run from a machine on last month's build — and a SELECT naming
    /// a column that database has never heard of throws <c>SqliteException: no such column</c> and
    /// takes the whole listing down with it. K3.3 adds four columns to <c>runs</c> and two to
    /// <c>sessions</c>; every one of them is read through this probe, so an older database degrades
    /// to "unrecorded" instead of "unreadable".
    /// <para><paramref name="table"/> is only ever a literal from this file — PRAGMA does not take a
    /// bound parameter, so it must be interpolated, and nothing user-supplied may reach it.</para>
    /// </summary>
    private HashSet<string> ColumnsOf(string table)
    {
        if (_columns.TryGetValue(table, out var known)) return known;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in Query($"PRAGMA table_info({table})"))
                if (row.TryGetValue("name", out var n) && n is string name)
                    found.Add(name);
        }
        catch (SqliteException)
        {
            // No such table — every Has() below answers false, which is the right degradation.
        }
        _columns[table] = found;
        return found;
    }

    private bool Has(string table, string column) => ColumnsOf(table).Contains(column);

    private readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every run in this database, newest first. A database holds more than one when a repo
    /// has been run repeatedly — which is the whole point of keeping it.</summary>
    public IReadOnlyList<ArchivedRun> Runs()
    {
        var provenance = Has("runs", "engine_version")
            ? "r.engine_version, r.engine_commit, r.engine_dirty, r.limits_json, "
            : "";
        // KS1.1 — v13's three, probed as one group because one migration adds all three. A v11 or v12
        // database answers "unrecorded" for the launch snapshot rather than taking the listing down,
        // which is the whole reason Has() exists.
        var launch = Has("runs", "limits_json_at_launch")
            ? "r.limits_json_at_launch, r.limits_reload_count, r.limits_reloaded_utc, "
            : "";
        var rows = Query(
            "SELECT r.run_id, r.plan_name, r.repo, r.branch, r.driver_ver, r.status, " +
            provenance + launch +
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
        var provenance = Has("sessions", "engine") ? "s.engine, s.limits, " : "";
        // K4.1: the recorded columns exist only from v12. Older archives answer from their event log
        // instead (below), so `history` reports a context profile for runs that finished long before
        // the measurement was written.
        var context = Has("sessions", "context_turns")
            ? "s.context_high_water, s.context_mean_turn, s.context_turns, " : "";
        // K4.2 reads newly_done to say which sessions closed a checkpoint. It has been in the schema
        // since v1, so every database this engine wrote has it — but the archive also opens databases
        // it did not write, and naming a column unconditionally is exactly what the probe exists to
        // prevent. Absent, it degrades to "unrecorded"; it does not take the listing down.
        var closed = Has("sessions", "newly_done") ? "s.newly_done, " : "";
        var rows = Query(
            "SELECT s.number, s.stage_id, s.kind, s.started_utc, s.ended_utc, s.outcome, s.attempt, " +
            "  s.resume_count, s.commit_count, s.result_summary, s.gate_summary, " +
            closed + provenance + context +
            "  (SELECT COALESCE(SUM(c.cost_usd), 0) FROM costs c " +
            "     WHERE c.run_id = s.run_id AND c.session_number = s.number) AS cost_usd, " +
            "  (SELECT COALESCE(SUM(c.tokens_in + c.tokens_out + c.tokens_think + c.tokens_cache), 0) " +
            "     FROM costs c WHERE c.run_id = s.run_id AND c.session_number = s.number) AS tokens, " +
            // K4.2: agent-category only, because that is the stream the session ceiling is compared
            // against. Gate and advisor rows carry cost but no tokens today, so this equals `tokens`
            // on every row in this repo — it stops being equal the moment an advisor lane reports.
            "  (SELECT COALESCE(SUM(c.tokens_in + c.tokens_out + c.tokens_think + c.tokens_cache), 0) " +
            "     FROM costs c WHERE c.run_id = s.run_id AND c.session_number = s.number " +
            "       AND c.category = 'agent') AS agent_tokens " +
            "FROM sessions s WHERE s.run_id = @runId ORDER BY s.number",
            ("@runId", runId));
        var sessions = rows.Select(MapSession).ToList();

        // K4.1: fill the gap from the deltas. A session recorded before v12 has no context columns, but
        // every API call it made is still in `events` as a TokenDelta, and Input (cache-creation
        // included) plus CacheRead is that call's prompt — the same arithmetic the live fold does. The
        // recorded columns win where they exist; this only speaks where they are silent.
        if (sessions.Any(s => s.Context is null))
        {
            var recovered = ContextFromEvents(runId);
            for (var i = 0; i < sessions.Count; i++)
                if (sessions[i].Context is null && recovered.TryGetValue(sessions[i].Number, out var c))
                    sessions[i] = sessions[i] with
                    {
                        ContextHighWater = c.HighWaterTokens,
                        ContextMeanTurn = c.MeanTurnTokens,
                        ContextTurns = c.Turns,
                    };
        }
        return sessions;
    }

    /// <summary>K4.1 — per-session context profile folded out of the archived TokenDelta events. Empty
    /// when the archive has no event log, or when its SQLite build cannot read JSON: a missing profile
    /// prints as "-", which is the truth, where a thrown query would take the whole view down.</summary>
    private Dictionary<int, Conductor.Core.Events.ContextWindowStats> ContextFromEvents(string runId)
    {
        var result = new Dictionary<int, Conductor.Core.Events.ContextWindowStats>();
        if (!Has("events", "payload")) return result;
        try
        {
            var rows = Query(
                "SELECT session_id, COUNT(*) AS turns, " +
                "  MAX(json_extract(payload, '$.input') + json_extract(payload, '$.cacheRead')) AS high, " +
                "  CAST(AVG(json_extract(payload, '$.input') + json_extract(payload, '$.cacheRead')) AS INTEGER) AS mean " +
                "FROM events WHERE run_id = @runId AND type = 'TokenDelta' AND session_id IS NOT NULL " +
                "GROUP BY session_id",
                ("@runId", runId));
            foreach (var r in rows)
            {
                if (!int.TryParse(r["session_id"] as string, out var number)) continue;
                var turns = Convert.ToInt32(r["turns"] ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                var high = Convert.ToInt64(r["high"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                var mean = Convert.ToInt64(r["mean"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                if (turns > 0 && high > 0) result[number] = new Conductor.Core.Events.ContextWindowStats(high, mean, turns);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // No JSON1, or an event log shaped differently. Nothing to report is a valid answer.
        }
        return result;
    }

    /// <summary>K4.2 — every firing of the cooperative nudge in one run, in order. Empty when the
    /// archive has no event log or its SQLite build cannot read JSON, which is the same "nothing to
    /// report" contract <see cref="ContextFromEvents"/> keeps: a budget with no nudge evidence still
    /// prints, it just says so instead of inventing a threshold.</summary>
    public IReadOnlyList<SoftBreakObservation> SoftBreaks(string runId)
    {
        var result = new List<SoftBreakObservation>();
        if (!Has("events", "payload")) return result;
        try
        {
            var rows = Query(
                "SELECT json_extract(e.payload, '$.liveTokens') AS live, " +
                "       json_extract(e.payload, '$.tokenBudget') AS budget, " +
                "       json_extract(e.payload, '$.currentCheckpointId') AS ckpt, " +
                "       (SELECT p.session_id FROM events p " +
                "          WHERE p.run_id = e.run_id AND p.type = 'SessionStarted' AND p.seq < e.seq " +
                "          ORDER BY p.seq DESC LIMIT 1) AS session_number " +
                "FROM events e WHERE e.run_id = @runId AND e.type = 'SoftBreakRequested' ORDER BY e.seq",
                ("@runId", runId));
            foreach (var r in rows)
            {
                if (!int.TryParse(r["session_number"] as string, out var number)) continue;
                if (r["live"] is not { } liveRaw) continue;
                var live = Convert.ToInt64(liveRaw, System.Globalization.CultureInfo.InvariantCulture);
                var budget = r["budget"] is { } b
                    ? Convert.ToInt64(b, System.Globalization.CultureInfo.InvariantCulture)
                    : (long?)null;
                result.Add(new SoftBreakObservation(number, live, budget > 0 ? budget : null, r["ckpt"] as string));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // No JSON1, or an event log shaped differently. Nothing to report is a valid answer.
        }
        return result;
    }

    /// <summary>K4.3 — every cost row of one run, in the order it was recorded. Per-row on purpose:
    /// the agent-versus-gate-versus-advisor split and the cache-read share are both lost the moment
    /// these are summed, and they are the two figures <c>conductor money</c> exists to print.</summary>
    public IReadOnlyList<ArchivedCost> Costs(string runId)
    {
        var rows = Query(
            "SELECT session_number, category, tokens_in, tokens_out, tokens_think, tokens_cache, " +
            "       cost_usd, wall_ms " +
            "FROM costs WHERE run_id = @runId ORDER BY session_number, id",
            ("@runId", runId));
        return rows.Select(r => new ArchivedCost(
            SessionNumber: Convert.ToInt32(r["session_number"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
            Category: (string)(r["category"] ?? "")!,
            TokensIn: Convert.ToInt64(r["tokens_in"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
            TokensOut: Convert.ToInt64(r["tokens_out"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
            TokensThink: Convert.ToInt64(r["tokens_think"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
            TokensCacheRead: Convert.ToInt64(r["tokens_cache"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
            CostUsd: Convert.ToDecimal(r["cost_usd"] ?? 0m, System.Globalization.CultureInfo.InvariantCulture),
            WallMs: Convert.ToInt64(r["wall_ms"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
    }

    /// <summary>
    /// The checkpoints of one run, folded out of the event log exactly the way the live store does
    /// it — the mutable <c>checkpoints</c> table was dropped in schema v8, so this fold IS the truth
    /// and re-deriving it here would be a second, divergent answer. The stage rows fold the same
    /// way (KS1.2, <c>RunArchive.Stages.cs</c>); both read the log through <see cref="EventsOf"/>.
    /// </summary>
    public IReadOnlyList<ArchivedCheckpoint> Checkpoints(string runId)
    {
        var graph = new TaskGraph();
        graph.Fold(EventsOf(runId));
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
        // K3.3: the structured column when the database has one; driver_ver is the fallback and, on a
        // v11 row, holds the same stamp as one string — so preferring engine_version is what keeps
        // EngineStampText from appending a commit to a version that already carries it.
        EngineVersion: Opt(r, "engine_version") as string ?? r["driver_ver"] as string,
        Status: (string)(r["status"] ?? "unknown")!,
        StartedUtc: r["started_utc"] as string,
        EndedUtc: r["ended_utc"] as string,
        LastActivityUtc: (r["last_session_utc"] as string) ?? (r["ended_utc"] as string) ?? (r["started_utc"] as string),
        Sessions: Convert.ToInt32(r["session_count"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
        CostUsd: Convert.ToDecimal(r["cost_usd"] ?? 0m, System.Globalization.CultureInfo.InvariantCulture),
        Tokens: Convert.ToInt64(r["tokens"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
        // K3.3 — absent on any database older than v11, hence Opt rather than the indexer.
        EngineCommit: Opt(r, "engine_commit") as string,
        EngineDirty: Opt(r, "engine_dirty") is { } d
            ? Convert.ToInt64(d, System.Globalization.CultureInfo.InvariantCulture) != 0
            : null,
        LimitsJson: Opt(r, "limits_json") as string,
        // KS1.1 — absent on anything older than v13, and absent is not zero for the count: a database
        // that never recorded a reload and one that recorded none read the same here on purpose,
        // because neither can claim the run was reloaded.
        LimitsAtLaunchJson: Opt(r, "limits_json_at_launch") as string,
        LimitsReloads: Opt(r, "limits_reload_count") is { } n
            ? Convert.ToInt32(n, System.Globalization.CultureInfo.InvariantCulture)
            : 0,
        LimitsReloadedUtc: Opt(r, "limits_reloaded_utc") as string);

    /// <summary>A column that may not exist in this database. <c>null</c> covers both "not selected"
    /// and "selected and NULL", which are the same answer to a reader: unrecorded.</summary>
    private static object? Opt(Dictionary<string, object?> r, string column)
        => r.TryGetValue(column, out var v) ? v : null;

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
        GateSummary: r["gate_summary"] as string,
        Engine: Opt(r, "engine") as string,
        LimitsJson: Opt(r, "limits") as string,
        ContextHighWater: OptLong(r, "context_high_water"),
        ContextMeanTurn: OptLong(r, "context_mean_turn"),
        ContextTurns: (int?)OptLong(r, "context_turns"),
        NewlyDone: Opt(r, "newly_done") as string,
        AgentTokens: Convert.ToInt64(r["agent_tokens"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>K4.1: an optional numeric column, null for "absent OR NULL" — the two are one answer.</summary>
    private static long? OptLong(Dictionary<string, object?> r, string column)
        => Opt(r, column) is { } v ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : null;
}
