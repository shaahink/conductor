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
        return rows.Select(r => new BugRow(
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
        )).ToList();
    }

    /// <summary>Transitions a bug's status (typically open→fixed), recording the session that closed it.
    /// Returns false if no such bug exists for this run (or the write failed).</summary>
    public bool UpdateBugStatus(string runId, long bugId, string status, int? fixedSession)
    {
        try
        {
            var rows = Query(
                "UPDATE bugs SET status = @status, fixed_session = @fixedSession, updated_at = datetime('now') " +
                "WHERE id = @id AND run_id = @runId RETURNING id",
                ("@runId", runId), ("@id", bugId), ("@status", status),
                ("@fixedSession", (object?)fixedSession ?? DBNull.Value));
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
