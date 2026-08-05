using System.Data;
using System.Globalization;

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
            StartedUtc: ParseUtc((string)r["started_utc"]!),
            ExitedUtc: r["exited_utc"] is string eu ? ParseUtc(eu) : null,
            ExitCode: r["exit_code"] is long ec ? (int?)ec : null,
            RunId: (string)r["run_id"]!
        )).ToList();
    }

    /// <summary>
    /// SC5.4 (round-four #4, the negative runtime): these two columns are written as round-trip UTC
    /// (<c>DateTime.ToString("O")</c> on a UTC instant, so <c>…Z</c>), and they were read back with a
    /// bare <c>DateTime.Parse</c>. That overload's default is <see cref="DateTimeStyles.None"/>, which
    /// CONVERTS a string carrying a timezone designator to the machine's local time and hands back
    /// <see cref="DateTimeKind.Local"/> — so every <c>PidRow.StartedUtc</c> was local time under a UTC
    /// name, and nothing in the type system said so.
    ///
    /// <para>What that cost, in the order it hurts:</para>
    /// <list type="bullet">
    /// <item>`bg status` and MCP `bg_status` compute a live row's runtime as
    /// <c>DateTime.UtcNow - StartedUtc</c> — a subtraction across two timezones. In UTC+1 a job
    /// running 32 minutes printed <c>-1694s</c>.</item>
    /// <item><see cref="PidLiveness.Check"/> compares the tracked start against the OS's real start
    /// time to spot a recycled pid. West of UTC the tracked value reads EARLIER than the true start,
    /// so every live tracked process answers <see cref="PidState.Recycled"/>: `bg status` shows dead,
    /// <see cref="PidLiveness.Sweep"/> buries live jobs, SC4.1's battery settle stops waiting for the
    /// session's bg children, and <c>ReapOrphans</c> never kills a real orphan. None of that is
    /// visible from a UTC+ machine, which is where this was written.</item>
    /// </list>
    ///
    /// <para><see cref="DateTimeStyles.AdjustToUniversal"/> normalises whatever offset the string
    /// carries to UTC and stamps <see cref="DateTimeKind.Utc"/>; <see cref="DateTimeStyles.AssumeUniversal"/>
    /// covers a legacy row written without a designator, which would otherwise be read as local and
    /// shifted. Display surfaces call <c>ToLocalTime()</c> explicitly now that the value is honestly
    /// UTC.</para>
    /// </summary>
    internal static DateTime ParseUtc(string s) => DateTime.Parse(
        s, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
