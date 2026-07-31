using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Store;

/// <summary>M7.2: a tracked bug — outlives the session that found it, injected into later prompts,
/// consumed by the audit phase.</summary>
public sealed record BugRow(
    long Id,
    string RunId,
    string Title,
    string? Detail,
    string Severity,
    string Status,
    string? StageId,
    int? FoundSession,
    int? FixedSession,
    string CreatedAt,
    string UpdatedAt
);

/// <summary>
/// SF0.4: an OPEN bug filed by an <b>earlier run</b> in this same <c>run.db</c>, carried forward with
/// the name of the plan that filed it.
///
/// <para>M7.2 promised a bug row that "outlives the session that found it" and delivered exactly that
/// — but every read was <c>WHERE run_id = @runId</c>, so a bug did <b>not</b> outlive the <i>run</i>
/// that found it. Measured 2026-07-31: the Sarban core run finished with eleven open bugs, the face
/// plan started a new run in the same repo, and <c>conductor bug list</c> answered with one row. No
/// error, no warning — an empty ledger that looks like a clean one, which is worse than a missing
/// feature because it reads as good news.</para>
/// </summary>
public sealed record CarriedBugRow(BugRow Bug, string PlanName);

public sealed partial class SqliteRunStore
{
    // ---------------------------------------------------------------- bugs (M7.2)

    /// <summary>Files a new tracked bug and returns its id (0 if the write failed). The row outlives
    /// the session that found it — later prompts inject the open ones and the audit phase consumes them.</summary>
    public long WriteBug(string runId, string title, string? detail, string severity, string? stageId, int? foundSession)
    {
        try
        {
            var rows = Query(
                "INSERT INTO bugs (run_id, title, detail, severity, status, stage_id, found_session) " +
                "VALUES (@runId, @title, @detail, @severity, 'open', @stageId, @foundSession) RETURNING id",
                ("@runId", runId), ("@title", title),
                ("@detail", (object?)detail ?? DBNull.Value),
                ("@severity", NormalizeSeverity(severity)),
                ("@stageId", (object?)stageId ?? DBNull.Value),
                ("@foundSession", (object?)foundSession ?? DBNull.Value));
            return rows.Count > 0 ? Convert.ToInt64(rows[0]["id"], CultureInfo.InvariantCulture) : 0;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogError(ex, "run.db bug write failed: {Title}", title);
            return 0;
        }
    }

    /// <summary>Reads tracked bugs newest-first, optionally filtered by status (open/fixed/wontfix).</summary>
    public IReadOnlyList<BugRow> QueryBugs(string runId, string? status = null)
    {
        var sql = "SELECT id, run_id, title, detail, severity, status, stage_id, found_session, fixed_session, created_at, updated_at " +
                  "FROM bugs WHERE run_id = @runId";
        var parameters = new List<(string, object?)> { ("@runId", runId) };
        if (status != null)
        {
            sql += " AND status = @status";
            parameters.Add(("@status", status));
        }
        sql += " ORDER BY id DESC";

        var rows = Query(sql, parameters.ToArray());
        return rows.Select(ToBugRow).ToList();
    }

    private static BugRow ToBugRow(Dictionary<string, object?> r) => new(
        Id: Convert.ToInt64(r["id"], CultureInfo.InvariantCulture),
        RunId: (string)r["run_id"]!,
        Title: (string)r["title"]!,
        Detail: r["detail"] as string,
        Severity: (string)r["severity"]!,
        Status: (string)r["status"]!,
        StageId: r["stage_id"] as string,
        FoundSession: r["found_session"] is long fs ? (int?)fs : null,
        FixedSession: r["fixed_session"] is long fx ? (int?)fx : null,
        CreatedAt: (string)(r["created_at"] ?? "")!,
        UpdatedAt: (string)(r["updated_at"] ?? "")!
    );

    /// <summary>SF0.4: every OPEN bug in this <c>run.db</c> that a run OTHER than <paramref name="currentRunId"/>
    /// filed, newest first, each paired with the plan that filed it. This is what makes a bug outlive the run
    /// that found it — see <see cref="CarriedBugRow"/> for what its absence cost. Closed bugs are not carried:
    /// the point is what is still outstanding, not a history of everything the repo ever hit.</summary>
    public IReadOnlyList<CarriedBugRow> QueryCarriedBugs(string currentRunId)
    {
        var rows = Query(
            "SELECT b.id, b.run_id, b.title, b.detail, b.severity, b.status, b.stage_id, b.found_session, " +
            "b.fixed_session, b.created_at, b.updated_at, r.plan_name " +
            "FROM bugs b LEFT JOIN runs r ON r.run_id = b.run_id " +
            "WHERE b.status = 'open' AND b.run_id <> @runId ORDER BY b.id DESC",
            ("@runId", currentRunId));
        return rows.Select(r => new CarriedBugRow(ToBugRow(r), r["plan_name"] as string ?? "")).ToList();
    }

    /// <summary>Transitions a bug's status (typically open→fixed), recording the session that closed it.
    /// Returns false if no such bug exists (or the write failed).
    ///
    /// <para>SF0.4: matched on <c>id</c> ALONE, not <c>(id, run_id)</c>. Ids are unique in this database, and
    /// a carried-forward bug — one an earlier run in this repo filed — has to be closable by the run that
    /// actually fixes it. Scoping the UPDATE to the current run meant <c>bug list</c> could show a row that no
    /// command could then close, which is a worse ledger than one that hides it. <paramref name="fixedSession"/>
    /// is only stamped when the closing run is the one that filed the bug — a session number from a different
    /// run points at the wrong session's history.</para></summary>
    public bool UpdateBugStatus(string runId, long bugId, string status, int? fixedSession)
    {
        try
        {
            var owner = Query("SELECT run_id FROM bugs WHERE id = @id", ("@id", bugId));
            if (owner.Count == 0) return false;
            var sameRun = string.Equals((string)owner[0]["run_id"]!, runId, StringComparison.Ordinal);

            var rows = Query(
                "UPDATE bugs SET status = @status, fixed_session = @fixedSession, updated_at = datetime('now') " +
                "WHERE id = @id RETURNING id",
                ("@id", bugId), ("@status", status),
                ("@fixedSession", sameRun ? (object?)fixedSession ?? DBNull.Value : DBNull.Value));
            return rows.Count > 0;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogError(ex, "run.db bug status update failed: bug {BugId}", bugId);
            return false;
        }
    }

    private static string NormalizeSeverity(string? severity)
    {
        var s = (severity ?? "").Trim().ToLowerInvariant();
        return s is "low" or "medium" or "high" ? s : "medium";
    }
}
