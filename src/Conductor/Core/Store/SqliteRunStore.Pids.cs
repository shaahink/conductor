using System.Data;

namespace Conductor.Core.Store;

public sealed partial class SqliteRunStore
{
    // ---------------------------------------------------------------- pids (F2.2: process tracking)

    public void TrackPid(int pid, string runId, string purpose, string? stageId, int? sessionNumber, DateTime startedUtc)
    {
        TryExecute(
            "INSERT INTO pids (pid, purpose, stage_id, session_number, started_utc, run_id) " +
            "VALUES (@pid, @purpose, @stageId, @sessionNumber, @startedUtc, @runId)",
            ("@pid", pid), ("@purpose", purpose),
            ("@stageId", (object?)stageId ?? DBNull.Value),
            ("@sessionNumber", (object?)sessionNumber ?? DBNull.Value),
            ("@startedUtc", startedUtc.ToString("O")),
            ("@runId", runId));
    }

    public void MarkPidExited(int pid, int? exitCode)
    {
        TryExecute(
            "UPDATE pids SET exited_utc = @exitedUtc, exit_code = @exitCode WHERE pid = @pid AND exited_utc IS NULL",
            ("@exitedUtc", _clock.GetUtcNow().ToString("O")),
            ("@exitCode", (object?)exitCode ?? DBNull.Value),
            ("@pid", pid));
    }

    public IReadOnlyList<OrphanPidRow> GetOrphanPids(string runId)
    {
        var rows = Query(
            "SELECT pid, purpose FROM pids WHERE run_id = @runId AND exited_utc IS NULL",
            ("@runId", runId));
        return rows.Select(r => new OrphanPidRow(
            Pid: Convert.ToInt32(r["pid"]),
            Purpose: (string)r["purpose"]!
        )).ToList();
    }

    public IReadOnlyList<PidRow> GetAllPids(string runId)
    {
        var rows = Query(
            "SELECT pid, purpose, stage_id, session_number, started_utc, exited_utc, exit_code, run_id FROM pids " +
            "WHERE run_id = @runId ORDER BY started_utc DESC",
            ("@runId", runId));
        return rows.Select(r => new PidRow(
            Pid: Convert.ToInt32(r["pid"]),
            Purpose: (string)r["purpose"]!,
            StageId: r["stage_id"] as string,
            SessionNumber: r["session_number"] is long sn ? (int?)sn : null,
            StartedUtc: DateTime.Parse((string)r["started_utc"]!),
            ExitedUtc: r["exited_utc"] is string eu ? DateTime.Parse(eu) : null,
            ExitCode: r["exit_code"] is long ec ? (int?)ec : null,
            RunId: (string)r["run_id"]!
        )).ToList();
    }
}
